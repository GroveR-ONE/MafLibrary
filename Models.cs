// Data model shared by the HTTP API, the SQLite storage and the JSON export.
// Field names are what the browser and the JSON export expect, so the storage
// implementation cannot change the API shape.

record PinRequest(string Pin);
record LoginRequest(string Username, string Password);
record CategoryRequest(string Category);
record ItemInput(string Category, string OrderNumber, string ProductNumber, string Name, string Notes, string Dimensions, string SourcePath);
record OrderInput(string Number, string Name);
record OrderNumberInput(string Number);

class LibraryData
{
    public List<string> Categories { get; set; } = new();
    public List<OrderReference> Orders { get; set; } = new();
    public List<Item> Items { get; set; } = new();
}

class OrderReference
{
    public string Number { get; set; } = "";
    public string ParkName { get; set; } = "";
}

class Item
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string OrderNumber { get; set; } = "";
    public string ProductNumber { get; set; } = "";
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Dimensions { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public List<Asset> Assets { get; set; } = new();

    // The full designation is computed, never stored: it cannot disagree with the
    // two parts. It is written to API responses and JSON exports, and skipped by
    // the storage serializer (see storageOptions in Program.cs).
    public string Number => !string.IsNullOrWhiteSpace(OrderNumber) && !string.IsNullOrWhiteSpace(ProductNumber) ? $"{OrderNumber}.{ProductNumber}" : "";
}

class Asset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string StoredName { get; set; } = "";
    public string Kind { get; set; } = "";
    public long Size { get; set; }
}
