using MessagePack;
using System;

namespace Stellar.Collections.Tests
{
    [MessagePackObject]
    public sealed class CustomerWithContract : IEquatable<CustomerWithContract>
    {
        [Key(0)]
        public int Id { get; set; }
        [Key(1)]
        public string Name { get; set; }
        [Key(3)]
        public int Telephone { get; set; }
        [Key(4)]
        public DateTime DateOfBirth { get; set; }

        public bool Equals(CustomerWithContract other)
        {
            return 
                Id.Equals(other.Id) &&
                Name.Equals(other.Name) &&
                Telephone.Equals(other.Telephone) &&
                DateOfBirth.Equals(other.DateOfBirth);
        }
    }
}