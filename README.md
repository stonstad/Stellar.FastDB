# Stellar.FastDB - High Performance Embedded Storage for C# (.NET)
Stellar.FastDB is an exceptionally fast document store for C# with speeds approximately 100 times faster than similar products. Designed for optimal performance and high concurrency, it excels in embedded workflows that demand efficiency.

---

<img width="863" alt="image" src="https://github.com/stonstad/Stellar.FastDB/assets/3117255/912f028e-0693-4717-a426-0ee0ce265ae1">

---

**Key Features**

- Serverless, embedded document storage.
- Simple, thread-safe API that supports asynchronous programming with async/await.
- Entirely written in C#, supporting .NET versions 5.0 through 8.0.
- Delivered as a compact, single DLL (60 kb).
- Enables multiple readers and writers simultaneously without the need for external locking.
- Schema-less NoSQL storage that adapts to changes.
- Optimized storage footprint that can be confused to use different formats (JSON, MessagePack).
- Advanced LZH compresion and AES encryption to ensure data security.
- Parallel processing capabilties for serialization, compression, and encryption.
- Supports reltional querying with LINQ.
- Ensures end-to-end type safety for data integrity.
- Accomodates composite keys for complex data structuring.
- Open source and free for both personal and commercial use.
- Install from Nuget. Install-Package Stellar.FastDB.

---

## Benchmarks

A comprehensive list of [benchmarks](https://github.com/stonstad/Stellar.Benchmarks/tree/main), along with a project for reproduction, is available.

### Insert
 Method      | Product | Op/s      | FileSize |
------------ |-------- |----------:|---------:|
 **Insert 10,000** | **FastDB**  | **192,855** | **653 KB** |
 Insert 10,000 | VistaDB |   2,648 |   940 KB |
 Insert 10,000 | LiteDB  |   1,251 | 1,656 KB |
 Insert 10,000 | SQLite  |     753 |   444 KB |
 
### Delete
 Method      | Product | Op/s      | FileSize |
------------ |-------- |----------:|---------:|
 **Delete 10,000** | **FastDB**  | **164,177** | **653 KB** |
 Delete 10,000 | VistaDB |   5,503 |   940 KB |
 Delete 10,000 | LiteDB  |   1,207 | 1,664 KB |
 Delete 10,000 | SQLite  |     757 |   444 KB |
 

### Upsert
 Method      | Product | Op/s      | FileSize |
------------ |-------- |----------:|---------:|
 **Upsert 10,000** | **FastDB**  | **93,633** | **653 KB** |
 Upsert 10,000 | LiteDB  |   3,192 | 1,664 KB |
 Upsert 10,000 | VistaDB |   2,372 |   940 KB |
 Upsert 10,000 | SQLite  |     741 |   444 KB |
 
### Bulk Insert
 Method    | Product | Op/s      | FileSize |
---------- |-------- |----------:|---------:|
 Bulk 10,000 | SQLite  | 294,455 |   444 KB |
 **Bulk 10,000** | **FastDB**  | **226,075** |   **653 KB** |
 Bulk 10,000 | LiteDB  |  44,219 |     8 KB |
 Bulk 10,000 | VistaDB |   2,706 |   952 KB |

### Query

 Method     | Product | Op/s         | FileSize |
----------- |-------- |-------------:|---------:|
 **Query 10,000** | **FastDB**  | **12,080,699** | **653 KB** |
 Query 10,000 | SQLite  |  2,227,601 |   444 KB |
 Query 10,000 | VistaDB |    574,299 |   940 KB |
 Query 10,000 | LiteDB  |    497,798 | 1,656 KB |
 
 ---

## Common Questions

**Why was Stellar.FasbDB created?**

As a game developer, I needed a high-concurrency storage solution suitable for player-managed game servers. Installing traditional local databases posed too high a demand on players. I found that the available storage solutions were either too slow or struggled with concurrency issues during high volumes of simultaneous reads and writes. Such limitations are impractical for game servers, which must support a large number of players efficiently.

**Should I use Stellar.FastDB?**

Use Stellar.FastDB if you need:
- Embedded, durable storage suitable for applications like game servers and desktop apps.
- Thread safety with robust supporst for high concurrency.
- High throughput for both reading and writing data.
- A minimal data storage footprint.

Do not use this database if you need:
- Sharing conections between processes.
- Storing data in a single file.

**What additional features are planned for Stellar.FastDB?**
- Integrated backup and restore functionality into a single file.
- A memory defragmentation algorithm to further optimize performance.
- An optional structured storage format with defined schema to further improve storage efficiency.
- Support for .NET Standard 2.1 and Unity.

## How to use Stellar.FastDB
- Stellar.FastDB's APIs are designed to be inuitive and closely resemble .NET collections. If you are familar with using a Dictionary, you'l find the traansiiton to FastDB seamless! By default, all writes to FastDB are immediate consistency, ensuring data integrity.
- To get started with Stellar.FastDB, install it from NuGet. Search for 'Stellar.FastDB' or use the package manager command 'Install-Package Stellar.FastDB'.
  
## Code Samples

### Getting Started

Below is a basic example demonstrating how to interact with Stellar.FastDB. This includes creating a database instance, storing customer data, updating it, retrieving it, and properly closing the database connection:

```C#
// create a class
public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; }
    public DateTime DOB { get; set; }
    public string Phone { get; set; }
    public bool IsActive { get; set; }
}

// create database
FastDB fastDB = new FastDB();

// create a collection (key, value)
var customers = fastDB.GetCollection<int, Customer>();

// create your new customer instance
var customer = new Customer
{ 
   Name = "John Wick", 
   Phone = "555-555-5555"
   DOB = new DateTime(2000, 1, 1)
   IsActive = true
};

// add customer
customers.Add(customer.Id, customer);

// update customer
customer.Name = "John Wick's Dog";
customers.Update(customer);

// use LINQ to query documents
var matches = customers.Where(a => a.Name.StartsWith("John") && a.Telephone > 5555555);

// close database
fastDB.Close();
```

### Encryption
```C#
FastDBOptions options = new FastDBOptions()
{
   IsEncrypted = true,
   EncryptionPassword = "open-sesame",
};
FastDB fastDB = new FastDB(options);
```

### Compression
```C#
FastDBOptions options = new FastDBOptions()
{
   IsCompressed = true,
};
FastDB fastDB = new FastDB(options);
```

### Parallel Data Transformation
For operations involving serialization, compression, or encryption of large object graphs, enabling parallel data transformations can significantly enhance throughput, especially with large records. This method effectively leverages multiple processor cores to accelerate write operations.

```C#
FastDBOptions options = new FastDBOptions()
{
   BufferMode = BufferModeType.WriteParallelEnabled,
   MaxDegreesOfParallelism = 8,
   IsEncryptionEnabled = true,
   EncryptionPassword = "open-sesame",
   IsCompressed = true,
};
FastDB fastDB = new FastDB(options);
```


|  Method                           | Product | Op/s      | FileSize |
|--------------------------------- |-------- |----------:|---------:|
| Large                            | FastDB  | 140,470 | 20,096 KB |
| Large Encrypted                   | FastDB  | 100,435 | 20,205 KB |
| Large Encrypted Compressed         | FastDB  |  68,064 | 14,892 KB |
| Large Enc Cmp Parallel | FastDB  | 138,588 | 14,892 KB |

### Serialization Contracts

To achieve the smallest possible storage footprint, Stellar.FastDB supports serialization contracts using MessagePack. By adding [MessagePack](https://github.com/MessagePack-CSharp/MessagePack-CSharp) attributes to your data models (as shown in the example below), you can instruct the serializer to package the data more efficiently. Note that this feature is disabled by default and is typically not included in most benchmarks.

```C#
// create a class
public class Customer
{
    [Key(0)]
    public int Id { get; set; }
    [Key(1)]
    public string Name { get; set; }
    [Key(2)]
    public DateTime DOB { get; set; }
    [Key(3)]
    public string Phone { get; set; }
    [Key(4)]
    public bool IsActive { get; set; }
}

FastDBOptions options = new FastDBOptions()
{
   Serializer = SerializerType.MessagePack_Contract,
};
FastDB fastDB = new FastDB(options);

// create a collection (key, value)
var customers = fastDB.GetCollection<int, Customer>();

// add customer
customers.Add(customer.Id, customer);
customres.Flush()

// close database
fastDB.Close();
```


| Serializer | Product | Op/s      | FileSize |
|--------- |-------- |----------:|---------:|
| Default  | FastDB  | 198,628 |   653 KB |
| Contract | FastDB  | 201,109 |   370 KB |
