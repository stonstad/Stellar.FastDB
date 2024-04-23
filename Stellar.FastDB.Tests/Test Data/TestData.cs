using System;
using System.Collections.Generic;

namespace Stellar.Collections.Tests
{
    public class TestData
    {
        public static int Seed0 = 0; // randomization is deterministic for benchmarking
        public static int Seed1 = 1;

        public static List<Customer> CreateCustomers(int n)
        {
            Random random = new Random(Seed0); 
            List<Customer> customers = new List<Customer>();
            for (int i = 1; i < n + 1; i++)
            {
                Customer customer = new Customer()
                {
                    Id = i,
                    Telephone = random.Next(1000000, 9999999),
                    DateOfBirth = new DateTime(2000, 1, 1) + TimeSpan.FromDays(i),
                    Name = $"John Doe {random.Next(100, 999)}"
                };

                customers.Add(customer);
            }

            return customers;
        }

        public static List<Customer> CreateCustomersLongText(int n)
        {
            Random random = new Random(Seed0);
            List<Customer> customers = new List<Customer>();
            for (int i = 1; i < n + 1; i++)
            {
                Customer customer = new Customer()
                {
                    Id = i,
                    Telephone = random.Next(1000000, 9999999),
                    DateOfBirth = new DateTime(2000, 1, 1) + TimeSpan.FromDays(i),
                    Name = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. In vel metus ut felis bibendum suscipit. " +
                        "Aliquam nibh purus, luctus eget pulvinar sed, accumsan in diam. Cras ornare commodo sem, eget imperdiet " +
                        "ligula ornare sit amet. Vivamus semper pulvinar nunc non cursus. Pellentesque pharetra nunc sit amet orci " +
                        "iaculis facilisis. Vestibulum placerat lectus at arcu pretium tempor ac sed mi. Donec fringilla pharetra " +
                        "dignissim. Suspendisse viverra arcu non iaculis interdum. Integer sapien lacus, interdum vitae egestas a, " +
                        "suscipit at ipsum. In laoreet sit amet purus at tincidunt. Quisque diam augue, congue ut ante vitae, rhoncus " +
                        "sollicitudin urna. Nullam volutpat pharetra sodales. Mauris lacinia semper imperdiet. Suspendisse sit amet " +
                        "auctor ante. Nunc a eleifend erat." +
                        "\r\n\r\n" +
                        "Proin interdum quam id ligula suscipit, varius rhoncus felis rutrum. Pellentesque in orci eu erat semper " +
                        "cursus. Maecenas urna eros, euismod et eros quis, porttitor ullamcorper turpis. Nunc vel feugiat enim, vitae " +
                        "interdum nibh. Morbi in sapien vitae turpis blandit accumsan. Etiam ornare eu turpis ac convallis. Donec " +
                        "fermentum nulla quis quam gravida, a imperdiet leo interdum. Curabitur sed euismod odio, et sollicitudin nisi. " +
                        "In erat nibh, interdum eget ullamcorper vitae, dapibus id odio. Fusce nec ante et ex elementum sagittis eu in " +
                        "justo. Curabitur at sapien neque. Suspendisse mollis odio sit amet libero egestas fermentum. Integer porttitor " +
                        "ex imperdiet erat vestibulum, sit amet accumsan nulla rhoncus. Etiam gravida vestibulum magna at lobortis. " +
                        "Vivamus iaculis molestie nunc, quis tristique sem auctor eget. Cras quis erat commodo, lobortis ante at, " +
                        "elementum elit." +
                        "\r\n\r\n" +
                        "In bibendum diam in nisi vulputate, suscipit consectetur erat euismod. Morbi luctus blandit arcu nec tristique. " +
                        "Class aptent taciti sociosqu ad litora torquent per conubia nostra, per inceptos himenaeos. Quisque ut est " +
                        "elit. In eu porttitor tellus. Duis vestibulum id nisi ac vestibulum. Sed vel sem sed lacus posuere hendrerit " +
                        "sit amet id metus.",
                };

                customers.Add(customer);
            }

            return customers;
        }

        public static List<CustomerWithContract> CreateCustomersWithSerializationContract(int n)
        {
            Random random = new Random(Seed0);
            List<CustomerWithContract> customers = new List<CustomerWithContract>();
            for (int i = 1; i < n + 1; i++)
            {
                CustomerWithContract customer = new CustomerWithContract()
                {
                    Id = i,
                    Telephone = random.Next(1000000, 9999999),
                    DateOfBirth = new DateTime(2000, 1, 1) + TimeSpan.FromDays(i),
                    Name = $"John Doe {random.Next(100, 999)}"
                };

                customers.Add(customer);
            }

            return customers;
        }
    }
}
