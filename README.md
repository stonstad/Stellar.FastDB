# Stellar.FastDB - High Performance Embedded Storage for C# (.NET)
The extremely fast Stellar.FastDB document store for C# is ~100x faster than its peers. FastDB is designed for performance and high concurrency.

---

<img width="864" alt="image" src="https://github.com/stonstad/Stellar.FastDB/assets/3117255/de7e5194-83ef-4b7d-9295-b5691053a146">


---

**Stellar.FastDB features ...**

- Serverless embedded document storage
- Simple thread-safe API with support for asynchronous programming (async/await)
- 100% C# code for .NET 5.0/6.0/7.0/8.0 delivered as a single DLL (60 kb)
- Supports multiple readers and writers without external locking
- Schema-less NoSQL storage with change resiliance
- Optimized and configurable storage footprint ([MessagePack](https://github.com/MessagePack-CSharp/MessagePack-CSharp), JSON)
- LZH Compresion and AES Encryption
- Parallelized serialization, compression, and encryption
- Relational querying through LINQ
- End-to-end type safety
- Support for composite keys
- Open source and free to use, including commerical use.
- Install from Nuget. Install-Package Stellar.FastDB

## Common Questions

** Why did you create Stellar.FasbDB?

I'm a game developer and I needed a high concurency storage solution for player-run game servers. Installing a local database would be asking too much of players. I discovered that existing storage solutions are slow and suffer from concurrency issues when there are too many readers and writers. This doesn't work for game servers which have large numbers of players reading and writing data.

**Should I use Stellar.FastDB?**

Use Stellar.FastDB if you need:
- Embedded durable storage (i.e. game server, desktop app)
- Thread-safety with high concurrency support
- High read and write throughput
- Small storage footprint

Do not use this database if you need:
- Interprocess connection sharing
- Single file storage

## Benchmarks

A complete list of [benchmarks](https://github.com/stonstad/Stellar.Benchmarks/tree/main) are available along with a reproduction project.

| Method      | Product | Op/s    | FileSize |
|------------ |-------- |--------:|---------:|
| Insert 10,000 | FastDB  | 197,899 |   653 KB |
| Insert 10,000 | LiteDB  |   1,300 |  1656 KB |
| Insert 10,000 | SQLite  |     754 |   444 KB |
| Insert 10,000 | VistaDB |   2,649 |  1244 KB |

---

## How to use Stellar.FastDB

FastDB's APIs are modelled after .NET collections. If you know how to use Dictionary you already know how to use FastDB! When using default settings, all FastDB writes are immediate and consistent.

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

|  Method                           | Product | Op/s      | FileSize |
|--------------------------------- |-------- |----------:|---------:|
| Large                            | FastDB  | 140,470.9 | 20096 KB |
| LargeEncrypted                   | FastDB  | 100,435.0 | 20205 KB |
| LargeEncryptedCompressed         | FastDB  |  68,064.7 | 14892 KB |
| LargeEncryptedCompressedParallel | FastDB  | 138,588.9 | 14892 KB |


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

### Message Contracts

If you would like the smallest storage footprint possible, serialization contracts using built-in [MessagePack](https://github.com/MessagePack-CSharp/MessagePack-CSharp) integration is supported. 

| Serializer | Product | Op/s      | FileSize |
|--------- |-------- |----------:|---------:|
| JSON     | FastDB  | 198,628.3 |   653 KB |
| Contract | FastDB  | 201,109.0 |   370 KB |

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
```

