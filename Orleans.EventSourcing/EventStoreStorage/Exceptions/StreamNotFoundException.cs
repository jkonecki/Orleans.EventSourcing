using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.EventSourcing.EventStoreStorage.Exceptions
{
    [Serializable]
    public class StreamNotFoundException : Exception
    {
        public StreamNotFoundException() { }
        public StreamNotFoundException(string message) : base(message) { }
        public StreamNotFoundException(string message, Exception inner) : base(message, inner) { }
        protected StreamNotFoundException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
    }
}
