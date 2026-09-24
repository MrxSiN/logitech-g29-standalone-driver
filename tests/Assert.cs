using System;

namespace G29.Tests
{
    internal static class Assert
    {
        internal static void True(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidOperationException("Assertion failed: " + name);
            }
        }

        internal static void False(bool value, string name)
        {
            True(!value, name);
        }

        internal static void Equal(int expected, int actual, string name)
        {
            if (expected != actual)
            {
                throw new InvalidOperationException(string.Format("Assertion failed: {0}. Expected {1}, got {2}.", name, expected, actual));
            }
        }

        internal static void Bytes(byte[] expected, byte[] actual, string name)
        {
            if (expected.Length != actual.Length)
            {
                throw new InvalidOperationException("Assertion failed: " + name + " length");
            }

            for (int index = 0; index < expected.Length; index++)
            {
                if (expected[index] != actual[index])
                {
                    throw new InvalidOperationException(string.Format("Assertion failed: {0} at byte {1}. Expected {2:X2}, got {3:X2}.", name, index, expected[index], actual[index]));
                }
            }
        }

        internal static void Throws<T>(Action action, string name) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }

            throw new InvalidOperationException("Assertion failed: " + name + " did not throw " + typeof(T).Name);
        }
    }
}

