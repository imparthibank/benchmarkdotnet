using BenchmarkDotNet.Attributes;
using Jil;
using Newtonsoft.Json;

namespace Benchmark.Playground.Models
{
    public class SerializationBenchmarks
    {
        private List<User> _randomPersons;
        private string json;

        public SerializationBenchmarks()
        {
            _randomPersons = new List<User>()
            {
                new User()
                {
                    FirstName = "John",
                    LastName = "Doe",
                    FullName = "Jone Doe",
                    Gender = Bogus.DataSets.Name.Gender.Male.ToString(),
                    Email = "jone.doe@gmail.com",
                    UserName="jone.doe@gmail.com",
                    DateOfBirth=new DateTime(1989,06,08),
                    Phone="9876543210"
                },
                new User()
                {
                    FirstName = "Ken",
                    LastName = "Devis",
                    FullName = "Ken Devis",
                    Gender = Bogus.DataSets.Name.Gender.Male.ToString(),
                    Email = "ken.devis@gmail.com",
                    UserName="ken.devis@gmail.com",
                    DateOfBirth=new DateTime(1956,02,21),
                    Phone="9876543210"
                }
            };
            json = JsonConvert.SerializeObject(_randomPersons); // Initial json for deserialization benchmarks
        }

        [Benchmark]
        public void NewtonsoftJsonSerialize()
        {
            string json = JsonConvert.SerializeObject(_randomPersons);
        }

        [Benchmark]
        public void NewtonsoftJsonDeserialize()
        {
            var person = JsonConvert.DeserializeObject<User>(json);
        }

        [Benchmark]
        public void SystemTextJsonSerialize()
        {
            string json = System.Text.Json.JsonSerializer.Serialize(_randomPersons);
        }

        [Benchmark]
        public void SystemTextJsonDeserialize()
        {
            var person = System.Text.Json.JsonSerializer.Deserialize<User>(json);
        }

        [Benchmark]
        public void JilSerialize()
        {
            using (var output = new StringWriter())
            {
                JSON.Serialize(_randomPersons, output);
            }
        }

        [Benchmark]
        public void JilDeserialize()
        {
            using (var input = new StringReader(json))
            {
                var person = JSON.Deserialize<User>(input);
            }
        }
    }
}
