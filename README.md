# Stellar.FastDB - a High Performance .NET Document Store
An embedded datatabase designed for high concurrency workloads.

---

Stellar.FastDB is an embedded document store built for performance and high concurrency.

- Serverless embedded document storage
- Simple thread-safe API
- 100% C# code for .NET 5,6,7,8.0 in a single DLL (less than 30 kb)
- Support multiple readers and writers with no explicit locking required.
- Schema-less storage with change resiliance.  
- Optimized storage footprint with configurable formats (MessagePack, BSON).  
- Supports asynchronous programming with async and await.
- LZH Compresion
- AES Encryption
- Confirgurable parallelization options for serialization, compression, and encryption.
- Relational querying through LINQ
- End-to-end type safety
- Supports for composite keys
- Open source and free to use, including commerical use.
- Install from Nuget. Install-Package Stellar.FastDB

## Should I use Stellar.FastDB?

Use Stellar.FastDB if you need:
- Embedded durable storage (i.e. game server, desktop app)
- Thread-safety and high in-process concurrency
- High throughput
- Small storage footprint

Do not use this database if you need:
- Interprocess connection sharing
- Single file storage


## How to use Stellar.FastDB

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
```

