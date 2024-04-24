# Stellar.FastDB - High Performance Embedded Storage for C# (.NET)
Stellar.FastDB is an exceptionally fast document store for C# with speeds approximately 100 times faster than similar products. Designed for optimal performance and high concurrency, it excels in embedded workflows that demand efficiency.

---

<img width="863" alt="image" src="https://github.com/stonstad/Stellar.FastDB/assets/3117255/765a4f19-8242-41d8-ae83-52eb26238e19">

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
- Install from Nuget. Install-Package Stellar.FastDB

---

## Benchmarks

A complete list of [benchmarks](https://github.com/stonstad/Stellar.Benchmarks/tree/main) are available along with a reproduction project.

**Insert**
| Method      | Product | Op/s    | FileSize |
|------------ |-------- |--------:|---------:|
| Insert 10,000 | FastDB  | 197,899 |   653 KB |
| Insert 10,000 | LiteDB  |   1,300 |  1,656 KB |
| Insert 10,000 | SQLite  |     754 |   444 KB |
| Insert 10,000 | VistaDB |   2,649 |  1,244 KB |

**Delete**

**Upsert**

---

## Common Questions

**Why did you create Stellar.FasbDB?**

I'm a game developer and I needed a high concurency storage solution for player-managed game servers. Installing a local database would be asking too much of players. I discovered that existing storage solutions are slow or suffer from concurrency issues when there are too many concurrent readers and writers. This doesn't work for game servers which support large numbers of players.

**Should I use Stellar.FastDB?**

Use Stellar.FastDB if you need:
- Embedded durable storage (i.e. game server, desktop app)
- Thread-safety with high concurrency support
- High read and write throughput
- Small storage footprint

Do not use this database if you need:
- Interprocess connection sharing
- Single file storage

** What additional features are on the roadmap? **

- Integrated backup and restore to a single file
- Memory defragmentation algorithm
- Optional structured storage mode with defined schmea for improved storage efficiency
- .NET Standard 2.1 support
- Unity support

## How to use Stellar.FastDB

- FastDB's APIs are modelled after .NET collections. If you know how to use Dictionary you already know how to use FastDB! When using default settings, all FastDB writes are immediate and consistent.
- Install Stellar.FastDB from Nuget by searching for Stellar.FastDB or running package manager command 'Install-Package Stellar.FastDB'. 

## Code Samples

### Getting Started

This example shows how to create a database instance, store a customer, update a customer, query for a customer, and lastly, close the database.

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
customer.Name = "Joana Doe";
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

### Parallel Serialization, Compression, Encryption
If you are serializing, compressing and/or encrypting large object graphs you may want to enable parallel transformations on the data. You'll see better throughput with large records.

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

### Message Contracts

If you would like smallest storage footprint possible, serialization contracts using [MessagePack](https://github.com/MessagePack-CSharp/MessagePack-CSharp) are supported. Adding MessagePack attributes (see example below) instruct the serializer how to better package the data. This option is disabled by default and is not included in most benchmarks.

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
