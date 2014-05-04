using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Test.Interfaces;

namespace Test.Implementation
{
    public class PersonRegistered
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public GenderType Gender { get; set; }

        public void Apply(IPersonState state)
        {
            state.FirstName = this.FirstName;
            state.LastName = this.LastName;
            state.Gender = this.Gender;
        }
    }

    public class PersonMarried
    {
        public string LastName { get; set; }

        public void Apply(IPersonState state)
        {
            state.LastName = this.LastName;
        }
    }
}
