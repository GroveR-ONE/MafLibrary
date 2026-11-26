using Microsoft.Data.Sqlite;

// SQLite storage for the catalog. The shape of the data is identical to the JSON
// storage: categories are strings, orders are number + park name, and every item
// keeps its original Id so uploaded files stay attached. The full item number is
// never stored, it is derived from the order number and the product number.
//
// Every write runs in a BEGIN IMMEDIATE transaction. The write lock is taken before
// the first read, so two requests cannot both observe "no such category" and then
// race to insert it. This replaces the old JSON read-modify-write pattern that could
// lose one of two concurrent saves.

enum StoreResult { Ok, NotFound, Conflict }

sealed class LibraryStore
{
    private readonly string connectionString;

    public LibraryStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
        EnsureSchema();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static SqliteTransaction BeginWrite(SqliteConnection connection) => connection.BeginTransaction(deferred: false);

    private void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS categories (
                id   INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE COLLATE NOCASE
            );

            CREATE TABLE IF NOT EXISTS orders (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                number    TEXT NOT NULL UNIQUE,
                park_name TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS items (
                id             TEXT PRIMARY KEY,
                category_id    INTEGER NOT NULL REFERENCES categories(id),
                order_id       INTEGER NOT NULL REFERENCES orders(id),
                product_number TEXT NOT NULL,
                name           TEXT NOT NULL,
                notes          TEXT NOT NULL DEFAULT '',
                dimensions     TEXT NOT NULL DEFAULT '',
                source_path    TEXT NOT NULL DEFAULT '',
                sort_order     INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS assets (
                id          TEXT PRIMARY KEY,
                item_id     TEXT NOT NULL REFERENCES items(id) ON DELETE CASCADE,
                name        TEXT NOT NULL,
                stored_name TEXT NOT NULL UNIQUE,
                kind        TEXT NOT NULL,
                size        INTEGER NOT NULL,
                sort_order  INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_items_category ON items(category_id);
            CREATE INDEX IF NOT EXISTS ix_items_order ON items(order_id);
            CREATE INDEX IF NOT EXISTS ix_assets_item ON assets(item_id);
            """;
        command.ExecuteNonQuery();
    }

    public bool IsEmpty()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM items) + (SELECT COUNT(*) FROM categories) + (SELECT COUNT(*) FROM orders);";
        return Convert.ToInt64(command.ExecuteScalar()) == 0;
    }

    // A marker, not an empty-count check, decides whether the first-run import is
    // allowed. Otherwise a deliberately cleared library would be restored from the
    // old JSON on the next start.
    public bool IsInitialized()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM meta WHERE key = 'initialized' LIMIT 1;";
        return command.ExecuteScalar() is not null;
    }

    public void MarkInitialized()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO meta (key, value) VALUES ('initialized', '1') ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.ExecuteNonQuery();
    }

    // Creates a consistent, single-file snapshot using SQLite's online backup API.
    // Writers may keep running: the copy contains one committed state, never a torn
    // page set. The snapshot is switched to DELETE journal mode so the archive needs
    // only one database entry and no -wal sidecar.
    public void BackupTo(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(destinationPath)) File.Delete(destinationPath);

        using (var source = Open())
        {
            var destinationString = new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString();
            using var destination = new SqliteConnection(destinationString);
            destination.Open();
            source.BackupDatabase(destination);
        }

        var snapshotString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        using (var snapshot = new SqliteConnection(snapshotString))
        {
            snapshot.Open();
            using (var checkpoint = snapshot.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
            }
            using (var mode = snapshot.CreateCommand())
            {
                mode.CommandText = "PRAGMA journal_mode = DELETE;";
                mode.ExecuteNonQuery();
            }
        }
        SqliteConnection.ClearAllPools();

        foreach (var sidecar in new[] { destinationPath + "-wal", destinationPath + "-shm" })
            if (File.Exists(sidecar)) File.Delete(sidecar);
    }

    // Writes everything from a JSON-shaped library in one transaction: either all of
    // it lands in the database or none of it does.
    public void ImportAll(LibraryData data)
    {
        var categories = data.Categories ?? new List<string>();
        var orders = data.Orders ?? new List<OrderReference>();
        var items = data.Items ?? new List<Item>();

        using var connection = Open();
        using var transaction = BeginWrite(connection);
        ClearTables(connection, transaction);

        var categoryIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var orderIds = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category)) continue;
            ResolveCategoryId(connection, transaction, categoryIds, category);
        }
        foreach (var order in orders)
        {
            if (string.IsNullOrWhiteSpace(order.Number)) continue;
            ResolveOrderId(connection, transaction, orderIds, order.Number, order.ParkName ?? "");
        }

        var sortOrder = 0;
        foreach (var item in items)
        {
            var categoryId = ResolveCategoryId(connection, transaction, categoryIds, item.Category);
            var orderId = ResolveOrderId(connection, transaction, orderIds, item.OrderNumber, "");
            InsertItem(connection, transaction, item, categoryId, orderId, sortOrder++);
        }

        transaction.Commit();
    }

    // Merges a JSON export into the existing catalog. Duplicates are matched by
    // category + order number + product number + name, case-insensitively, exactly
    // like the JSON version did. The whole merge is one transaction.
    public (int Added, int Skipped) MergeImport(LibraryData imported, string uploadsRoot)
    {
        var categories = imported.Categories ?? new List<string>();
        var orders = imported.Orders ?? new List<OrderReference>();
        var items = imported.Items ?? new List<Item>();

        using var connection = Open();
        using var transaction = BeginWrite(connection);

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT c.name, o.number, i.product_number, i.name
                FROM items i
                JOIN categories c ON c.id = i.category_id
                JOIN orders o ON o.id = i.order_id;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                existing.Add(DuplicateKey(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        var categoryIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var orderIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category)) continue;
            ResolveCategoryId(connection, transaction, categoryIds, category);
        }
        foreach (var order in orders)
        {
            if (string.IsNullOrWhiteSpace(order.Number)) continue;
            var found = FindOrder(connection, transaction, order.Number);
            if (found is null) ResolveOrderId(connection, transaction, orderIds, order.Number, order.ParkName ?? "");
            else orderIds[order.Number] = found.Value.Id;
        }

        var sortOrder = NextSortOrder(connection, transaction, "items", "sort_order", "");
        var added = 0;
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.OrderNumber) || string.IsNullOrWhiteSpace(item.ProductNumber)) continue;
            if (!existing.Add(DuplicateKey(item.Category, item.OrderNumber, item.ProductNumber, item.Name))) continue;

            PrepareImportedAssets(connection, transaction, item, uploadsRoot);
            var categoryId = ResolveCategoryId(connection, transaction, categoryIds, item.Category);
            var orderId = ResolveOrderId(connection, transaction, orderIds, item.OrderNumber, "");
            InsertItem(connection, transaction, item, categoryId, orderId, sortOrder++);
            added++;
        }

        transaction.Commit();
        return (added, items.Count - added);
    }

    public LibraryData Load()
    {
        using var connection = Open();
        return ReadLibrary(connection);
    }

    // Reads a database file without touching the configured connection string. Used
    // to export the JSON that belongs to a backup snapshot.
    public static LibraryData Read(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        return ReadLibrary(connection);
    }

    private static LibraryData ReadLibrary(SqliteConnection connection)
    {
        var data = new LibraryData();

        using (var categories = connection.CreateCommand())
        {
            categories.CommandText = "SELECT name FROM categories ORDER BY id;";
            using var reader = categories.ExecuteReader();
            while (reader.Read()) data.Categories.Add(reader.GetString(0));
        }
        using (var orders = connection.CreateCommand())
        {
            orders.CommandText = "SELECT number, park_name FROM orders ORDER BY id;";
            using var reader = orders.ExecuteReader();
            while (reader.Read()) data.Orders.Add(new OrderReference { Number = reader.GetString(0), ParkName = reader.GetString(1) });
        }
        using (var items = connection.CreateCommand())
        {
            items.CommandText = """
                SELECT i.id, c.name, o.number, i.product_number, i.name, i.notes, i.dimensions, i.source_path
                FROM items i
                JOIN categories c ON c.id = i.category_id
                JOIN orders o ON o.id = i.order_id
                ORDER BY i.sort_order, i.rowid;
                """;
            using var reader = items.ExecuteReader();
            while (reader.Read())
            {
                data.Items.Add(new Item
                {
                    Id = reader.GetString(0),
                    Category = reader.GetString(1),
                    OrderNumber = reader.GetString(2),
                    ProductNumber = reader.GetString(3),
                    Name = reader.GetString(4),
                    Notes = reader.GetString(5),
                    Dimensions = reader.GetString(6),
                    SourcePath = reader.GetString(7),
                });
            }
        }
        using (var assets = connection.CreateCommand())
        {
            assets.CommandText = "SELECT id, item_id, name, stored_name, kind, size FROM assets ORDER BY sort_order, rowid;";
            var byItem = data.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
            using var reader = assets.ExecuteReader();
            while (reader.Read())
            {
                if (!byItem.TryGetValue(reader.GetString(1), out var item)) continue;
                item.Assets.Add(new Asset
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(2),
                    StoredName = reader.GetString(3),
                    Kind = reader.GetString(4),
                    Size = reader.GetInt64(5),
                });
            }
        }
        return data;
    }

    public Item? GetItem(string id)
    {
        using var connection = Open();
        Item? item = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT i.id, c.name, o.number, i.product_number, i.name, i.notes, i.dimensions, i.source_path
                FROM items i
                JOIN categories c ON c.id = i.category_id
                JOIN orders o ON o.id = i.order_id
                WHERE i.id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                item = new Item
                {
                    Id = reader.GetString(0),
                    Category = reader.GetString(1),
                    OrderNumber = reader.GetString(2),
                    ProductNumber = reader.GetString(3),
                    Name = reader.GetString(4),
                    Notes = reader.GetString(5),
                    Dimensions = reader.GetString(6),
                    SourcePath = reader.GetString(7),
                };
            }
        }
        if (item is null) return null;

        using (var assets = connection.CreateCommand())
        {
            assets.CommandText = "SELECT id, name, stored_name, kind, size FROM assets WHERE item_id = $id ORDER BY sort_order, rowid;";
            assets.Parameters.AddWithValue("$id", id);
            using var reader = assets.ExecuteReader();
            while (reader.Read())
            {
                item.Assets.Add(new Asset
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    StoredName = reader.GetString(2),
                    Kind = reader.GetString(3),
                    Size = reader.GetInt64(4),
                });
            }
        }
        return item;
    }

    public OrderReference? FindOrder(string number)
    {
        using var connection = Open();
        var found = FindOrder(connection, null, number);
        return found is null ? null : new OrderReference { Number = found.Value.Number, ParkName = found.Value.ParkName };
    }

    public string AddItem(Item item)
    {
        if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");
        using var connection = Open();
        using var transaction = BeginWrite(connection);
        var categoryIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var orderIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var categoryId = ResolveCategoryId(connection, transaction, categoryIds, item.Category);
        var orderId = ResolveOrderId(connection, transaction, orderIds, item.OrderNumber, "");
        var next = NextSortOrder(connection, transaction, "items", "sort_order", "");
        InsertItem(connection, transaction, item, categoryId, orderId, next);
        transaction.Commit();
        return item.Id;
    }

    public bool UpdateItem(Item item)
    {
        using var connection = Open();
        using var transaction = BeginWrite(connection);
        if (!ItemExists(connection, transaction, item.Id)) { transaction.Rollback(); return false; }

        var categoryId = ResolveCategoryId(connection, transaction, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase), item.Category);
        var orderId = ResolveOrderId(connection, transaction, new Dictionary<string, long>(StringComparer.Ordinal), item.OrderNumber, "");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE items SET category_id = $category, order_id = $order, product_number = $product,
                             name = $name, notes = $notes, dimensions = $dimensions, source_path = $source
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$category", categoryId);
        command.Parameters.AddWithValue("$order", orderId);
        command.Parameters.AddWithValue("$product", item.ProductNumber ?? "");
        command.Parameters.AddWithValue("$name", item.Name ?? "");
        command.Parameters.AddWithValue("$notes", item.Notes ?? "");
        command.Parameters.AddWithValue("$dimensions", item.Dimensions ?? "");
        command.Parameters.AddWithValue("$source", item.SourcePath ?? "");
        command.Parameters.AddWithValue("$id", item.Id);
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    // Returns the stored file names that belong to the deleted item, or null when the
    // item does not exist. Assets disappear through ON DELETE CASCADE in one transaction.
    public List<string>? DeleteItem(string id)
    {
        using var connection = Open();
        using var transaction = BeginWrite(connection);
        if (!ItemExists(connection, transaction, id)) { transaction.Rollback(); return null; }

        var stored = StoredNames(connection, transaction, id);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        transaction.Commit();
        return stored;
    }

    public bool ItemExists(string id)
    {
        using var connection = Open();
        return ItemExists(connection, null, id);
    }

    public void AddCategory(string name)
    {
        var value = (name ?? "").Trim();
        using var connection = Open();
        using var transaction = BeginWrite(connection);
        if (FindCategory(connection, transaction, value) is not null) { transaction.Commit(); return; }

        try
        {
            InsertCategory(connection, transaction, value);
            transaction.Commit();
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            // The write lock prevents this in practice; a UNIQUE conflict here means
            // the same ASCII-case name already exists and the result is the same.
            transaction.Rollback();
        }
    }

    public StoreResult RenameCategory(string oldName, string newName)
    {
        using var connection = Open();
        using var transaction = BeginWrite(connection);
        var current = FindCategory(connection, transaction, oldName);
        if (current is null) { transaction.Rollback(); return StoreResult.NotFound; }
        var target = FindCategory(connection, transaction, newName);
        if (target is not null && target.Value.Id != current.Value.Id) { transaction.Rollback(); return StoreResult.Conflict; }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE categories SET name = $name WHERE id = $id;";
        command.Parameters.AddWithValue("$name", newName);
        command.Parameters.AddWithValue("$id", current.Value.Id);
        command.ExecuteNonQuery();
        transaction.Commit();
        return StoreResult.Ok;
    }

    public StoreResult DeleteCategory(string name)
    {
        using var connection = Open();
        using var transaction = BeginWrite(connection);
        var current = FindCategory(connection, transaction, name);
        if (current is null) { transaction.Rollback(); return StoreResult.NotFound; }
        if (CountItemsForCategory(connection, transaction, current.Value.Id) > 0) { transaction.Rollback(); return StoreResult.Conflict; }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM categories WHERE id = $id;";
        command.Parameters.AddWithValue("$id", current.Value.Id);
        command.ExecuteNonQuery();
        transaction.Commit();
        return StoreResult.Ok;
    }

    public int CountItemsInCategory(string name)
    {
        using var connection = Open();
        var current = FindCategory(connection, null, name);
        return current is null ? 0 : CountItemsForCategory(connection, null, current.Value.Id);
    }

    public void AddOrder(string number, string parkName)
    {
        var value = (number ?? "").Trim();
        var name = (parkName ?? "").Trim();
        using var connection = Open();
        using var transaction = BeginWrite(connection);
        var existing = FindOrder(connection, transaction, value);
        if (existing is not null)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE orders SET park_name = $name WHERE id = $id;";
            update.Parameters.AddWithValue("$name", name);
            update.Parameters.AddWithValue("$id", existing.Value.Id);
            update.ExecuteNonQuery();
        }
        else
        {
            InsertOrder(connection, transaction, value, name);
        }
        transaction.Commit();
    }

    public OrderReference? UpdateOrder(string number, string parkName)
    {
        var name = (parkName ?? "").Trim();
        using var connection = Open();
        using var transaction = BeginWrite(connection);
        var existing = FindOrder(connection, transaction, number);
        if (existing is null) { transaction.Rollback(); return null; }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE orders SET park_name = $name WHERE id = $id;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$id", existing.Value.Id);
        command.ExecuteNonQuery();
        transaction.Commit();
        return new OrderReference { Number = existing.Value.Number, ParkName = name };
    }

    public StoreResult RenameOrderNumber(string oldNumber, string newNumber)
    {
        using var connection = Open();
        using var transaction = BeginWrite(connection);
        var current = FindOrder(connection, transaction, oldNumber);
        if (current is null) { transaction.Rollback(); return StoreResult.NotFound; }
        var target = FindOrder(connection, transaction, newNumber);
        if (target is not null && target.Value.Id != current.Value.Id) { transaction.Rollback(); return StoreResult.Conflict; }

        // Item designations follow automatically: the order number is joined at read time.
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE orders SET number = $new WHERE id = $id;";
        command.Parameters.AddWithValue("$new", newNumber);
        command.Parameters.AddWithValue("$id", current.Value.Id);
        command.ExecuteNonQuery();
        transaction.Commit();
        return StoreResult.Ok;
    }

    public StoreResult DeleteOrder(string number)
    {
        using var connection = Open();
        using var transaction = BeginWrite(connection);
        var current = FindOrder(connection, transaction, number);
        if (current is null) { transaction.Rollback(); return StoreResult.NotFound; }
        if (CountItemsForOrder(connection, transaction, current.Value.Id) > 0) { transaction.Rollback(); return StoreResult.Conflict; }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM orders WHERE id = $id;";
        command.Parameters.AddWithValue("$id", current.Value.Id);
        command.ExecuteNonQuery();
        transaction.Commit();
        return StoreResult.Ok;
    }

    public int CountItemsInOrder(string number)
    {
        using var connection = Open();
        var current = FindOrder(connection, null, number);
        return current is null ? 0 : CountItemsForOrder(connection, null, current.Value.Id);
    }

    // Adds an asset. When replaceKind is set, all assets of that kind are removed in
    // the same transaction, so a single-file slot can never briefly hold two files.
    // Returns the stored names that were removed, or null when the item is gone.
    public List<string>? AddAsset(string itemId, Asset asset, string? replaceKind = null)
    {
        using var connection = Open();
        using var transaction = BeginWrite(connection);
        if (!ItemExists(connection, transaction, itemId)) { transaction.Rollback(); return null; }

        var removed = new List<string>();
        if (!string.IsNullOrEmpty(replaceKind))
        {
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT stored_name FROM assets WHERE item_id = $item AND kind = $kind ORDER BY sort_order, rowid;";
                select.Parameters.AddWithValue("$item", itemId);
                select.Parameters.AddWithValue("$kind", replaceKind);
                using var reader = select.ExecuteReader();
                while (reader.Read()) removed.Add(reader.GetString(0));
            }
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM assets WHERE item_id = $item AND kind = $kind;";
            delete.Parameters.AddWithValue("$item", itemId);
            delete.Parameters.AddWithValue("$kind", replaceKind);
            delete.ExecuteNonQuery();
        }

        if (string.IsNullOrWhiteSpace(asset.Id)) asset.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(asset.StoredName)) asset.StoredName = Guid.NewGuid().ToString("N");
        var next = NextSortOrder(connection, transaction, "assets", "sort_order", itemId);
        InsertAsset(connection, transaction, itemId, asset, next);
        transaction.Commit();
        return removed;
    }

    // Returns the stored file name that belongs to the removed asset, or null.
    public string? DeleteAsset(string itemId, string assetId)
    {
        using var connection = Open();
        using var transaction = BeginWrite(connection);
        string? stored;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT stored_name FROM assets WHERE id = $id AND item_id = $item;";
            select.Parameters.AddWithValue("$id", assetId);
            select.Parameters.AddWithValue("$item", itemId);
            stored = select.ExecuteScalar() as string;
        }
        if (stored is null) { transaction.Rollback(); return null; }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM assets WHERE id = $id AND item_id = $item;";
        command.Parameters.AddWithValue("$id", assetId);
        command.Parameters.AddWithValue("$item", itemId);
        command.ExecuteNonQuery();
        transaction.Commit();
        return stored;
    }

    // Clears the catalog but keeps the initialized marker. Returns the stored files
    // so the caller can remove them after the database transaction has committed.
    public List<string> Clear()
    {
        using var connection = Open();
        using var transaction = BeginWrite(connection);
        var stored = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT stored_name FROM assets ORDER BY sort_order, rowid;";
            using var reader = select.ExecuteReader();
            while (reader.Read()) stored.Add(reader.GetString(0));
        }
        ClearTables(connection, transaction);
        transaction.Commit();
        return stored;
    }

    private static void ClearTables(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM assets; DELETE FROM items; DELETE FROM orders; DELETE FROM categories;";
        command.ExecuteNonQuery();
    }

    private static List<string> StoredNames(SqliteConnection connection, SqliteTransaction transaction, string itemId)
    {
        var stored = new List<string>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT stored_name FROM assets WHERE item_id = $id ORDER BY sort_order, rowid;";
        command.Parameters.AddWithValue("$id", itemId);
        using var reader = command.ExecuteReader();
        while (reader.Read()) stored.Add(reader.GetString(0));
        return stored;
    }

    private static void PrepareImportedAssets(SqliteConnection connection, SqliteTransaction transaction, Item item, string uploadsRoot)
    {
        item.Assets ??= new List<Asset>();
        foreach (var asset in item.Assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Id) || AssetIdExists(connection, transaction, asset.Id))
                asset.Id = Guid.NewGuid().ToString("N");

            if (string.IsNullOrWhiteSpace(asset.StoredName))
            {
                asset.StoredName = Guid.NewGuid().ToString("N");
                continue;
            }
            if (!StoredNameExists(connection, transaction, asset.StoredName)) continue;

            // The same file cannot be linked twice: deleting one asset would break the
            // other. Give the imported asset its own copy when the file is present.
            var extension = Path.GetExtension(asset.StoredName);
            var replacement = Guid.NewGuid().ToString("N") + extension;
            var source = Path.Combine(uploadsRoot, Path.GetFileName(asset.StoredName));
            var target = Path.Combine(uploadsRoot, replacement);
            if (File.Exists(source) && !File.Exists(target))
            {
                try { File.Copy(source, target); }
                catch (IOException) { }
            }
            asset.StoredName = replacement;
        }
    }

    private static bool AssetIdExists(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM assets WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is not null;
    }

    private static bool StoredNameExists(SqliteConnection connection, SqliteTransaction transaction, string storedName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM assets WHERE stored_name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", storedName);
        return command.ExecuteScalar() is not null;
    }

    private static string DuplicateKey(string category, string orderNumber, string productNumber, string name)
        => $"{category}\u001f{orderNumber}\u001f{productNumber}\u001f{name}";

    private static (long Id, string Name)? FindCategory(SqliteConnection connection, SqliteTransaction? transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name FROM categories;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var stored = reader.GetString(1);
            if (string.Equals(stored, name, StringComparison.OrdinalIgnoreCase)) return (reader.GetInt64(0), stored);
        }
        return null;
    }

    private static (long Id, string Number, string ParkName)? FindOrder(SqliteConnection connection, SqliteTransaction? transaction, string number)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, number, park_name FROM orders;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var stored = reader.GetString(1);
            if (string.Equals(stored, number, StringComparison.OrdinalIgnoreCase)) return (reader.GetInt64(0), stored, reader.GetString(2));
        }
        return null;
    }

    private static long InsertCategory(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO categories (name) VALUES ($name); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long InsertOrder(SqliteConnection connection, SqliteTransaction transaction, string number, string parkName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO orders (number, park_name) VALUES ($number, $name); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$number", number);
        command.Parameters.AddWithValue("$name", parkName ?? "");
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void InsertItem(SqliteConnection connection, SqliteTransaction transaction, Item item, long categoryId, long orderId, int sortOrder)
    {
        if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");
        item.Assets ??= new List<Asset>();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO items (id, category_id, order_id, product_number, name, notes, dimensions, source_path, sort_order)
                VALUES ($id, $category, $order, $product, $name, $notes, $dimensions, $source, $sort);
                """;
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$category", categoryId);
            command.Parameters.AddWithValue("$order", orderId);
            command.Parameters.AddWithValue("$product", item.ProductNumber ?? "");
            command.Parameters.AddWithValue("$name", item.Name ?? "");
            command.Parameters.AddWithValue("$notes", item.Notes ?? "");
            command.Parameters.AddWithValue("$dimensions", item.Dimensions ?? "");
            command.Parameters.AddWithValue("$source", item.SourcePath ?? "");
            command.Parameters.AddWithValue("$sort", sortOrder);
            command.ExecuteNonQuery();
        }

        var assetOrder = 0;
        foreach (var asset in item.Assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Id)) asset.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(asset.StoredName)) asset.StoredName = Guid.NewGuid().ToString("N");
            InsertAsset(connection, transaction, item.Id, asset, assetOrder++);
        }
    }

    private static void InsertAsset(SqliteConnection connection, SqliteTransaction transaction, string itemId, Asset asset, int sortOrder)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO assets (id, item_id, name, stored_name, kind, size, sort_order)
            VALUES ($id, $item, $name, $stored, $kind, $size, $order);
            """;
        command.Parameters.AddWithValue("$id", asset.Id);
        command.Parameters.AddWithValue("$item", itemId);
        command.Parameters.AddWithValue("$name", asset.Name ?? "");
        command.Parameters.AddWithValue("$stored", asset.StoredName);
        command.Parameters.AddWithValue("$kind", asset.Kind ?? "");
        command.Parameters.AddWithValue("$size", asset.Size);
        command.Parameters.AddWithValue("$order", sortOrder);
        command.ExecuteNonQuery();
    }

    private static long ResolveCategoryId(SqliteConnection connection, SqliteTransaction transaction, Dictionary<string, long> cache, string name)
    {
        var key = (name ?? "").Trim();
        if (cache.TryGetValue(key, out var cached)) return cached;

        var existing = FindCategory(connection, transaction, key);
        long id;
        if (existing is not null)
        {
            id = existing.Value.Id;
        }
        else
        {
            try
            {
                id = InsertCategory(connection, transaction, key);
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 19)
            {
                var retry = FindCategory(connection, transaction, key);
                if (retry is null) throw;
                id = retry.Value.Id;
            }
        }
        cache[key] = id;
        return id;
    }

    private static long ResolveOrderId(SqliteConnection connection, SqliteTransaction transaction, Dictionary<string, long> cache, string number, string parkName)
    {
        var key = (number ?? "").Trim();
        if (cache.TryGetValue(key, out var cached)) return cached;

        var existing = FindOrder(connection, transaction, key);
        long id;
        if (existing is not null)
        {
            id = existing.Value.Id;
        }
        else
        {
            try
            {
                id = InsertOrder(connection, transaction, key, parkName ?? "");
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 19)
            {
                var retry = FindOrder(connection, transaction, key);
                if (retry is null) throw;
                id = retry.Value.Id;
            }
        }
        cache[key] = id;
        return id;
    }

    private static bool ItemExists(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM items WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is not null;
    }

    private static int CountItemsForCategory(SqliteConnection connection, SqliteTransaction? transaction, long categoryId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM items WHERE category_id = $id;";
        command.Parameters.AddWithValue("$id", categoryId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int CountItemsForOrder(SqliteConnection connection, SqliteTransaction? transaction, long orderId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM items WHERE order_id = $id;";
        command.Parameters.AddWithValue("$id", orderId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int NextSortOrder(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string itemId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = itemId.Length == 0
            ? $"SELECT COALESCE(MAX({column}), -1) + 1 FROM {table};"
            : $"SELECT COALESCE(MAX({column}), -1) + 1 FROM {table} WHERE item_id = $item;";
        if (itemId.Length > 0) command.Parameters.AddWithValue("$item", itemId);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
