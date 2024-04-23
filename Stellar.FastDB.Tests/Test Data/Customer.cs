using System;

namespace Stellar.Collections.Tests
{
    public sealed class Customer : IEquatable<Customer>
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Telephone { get; set; }
        public DateTime DateOfBirth { get; set; }

        public bool Equals(Customer other)
        {
            return 
                Id.Equals(other.Id) &&
                Name.Equals(other.Name) &&
                Telephone.Equals(other.Telephone) &&
                DateOfBirth.Equals(other.DateOfBirth);
        }
    }
}