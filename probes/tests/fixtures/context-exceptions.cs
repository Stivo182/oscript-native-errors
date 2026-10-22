using OneScript.Contexts;
using OneScript.Exceptions;

namespace Tests
{
    [ContextClass("TestObject")]
    public class TestObject
    {
        [ContextMethod("Read")]
        public void Read()
        {
            throw new System.IO.IOException("File busy");
        }

        [ContextMethod("Configure")]
        public void Configure()
        {
            throw RuntimeException.InvalidArgumentValue();
        }

        [ContextMethod("Copy")]
        public void Copy()
        {
            try
            {
                Read();
            }
            catch (System.IO.IOException)
            {
                throw;
            }
        }

        [ContextMethod("Write")]
        public void Write()
        {
            throw RuntimeException.ClosedStream("https://example.test/stream");
        }

        [ContextMethod("Retry")]
        public void Retry()
        {
            try
            {
                Read();
            }
            catch (System.IO.IOException)
            {
                try
                {
                    Read();
                }
                catch (System.TimeoutException)
                {
                }

                throw;
            }
        }

        [ContextMethod("Validate")]
        public void Validate()
        {
            throw new System.InvalidOperationException("State is invalid");
        }

        [ContextMethod("Compile")]
        public void Compile()
        {
            throw CompilerException.Failed();
        }

        // throw RuntimeException.ClosedStream();
    }
}
