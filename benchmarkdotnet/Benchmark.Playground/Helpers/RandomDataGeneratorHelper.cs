using Benchmark.Playground.Models;
using Bogus;

namespace Benchmark.Playground.Helpers
{
    public class RandomDataGeneratorHelper
    {
        public List<User> GenerateRandomPersons(int listSize)
        {
            var personFaker = new Faker<User>()
                              .RuleFor(p => p.FirstName, f => f.Name.FirstName())
                              .RuleFor(p => p.LastName, f => f.Name.LastName())
                              .RuleFor(p => p.Gender, f => f.Person.Gender.ToString())
                              .RuleFor(p => p.FullName, (f, p) => $"{p.FirstName} {p.LastName}")
                              .RuleFor(p => p.UserName, (f, p) => f.Internet.UserName(p.FirstName, p.LastName))
                              .RuleFor(p => p.Email, (f, p) => f.Internet.Email(p.FirstName, p.LastName, "Yhoo.com"))
                              .RuleFor(p => p.DateOfBirth, f => f.Date.Past(50, DateTime.Now.AddYears(-18)))
                              .RuleFor(p => p.Phone, f => f.Phone.PhoneNumber("'###-###-####"));

            return personFaker.Generate(listSize);
        }
    }
}
