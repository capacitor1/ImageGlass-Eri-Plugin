namespace EriCodec;
internal class InvalidFormatException : FormatException
{
    public InvalidFormatException() { }
    public InvalidFormatException(string msg) : base(msg) { }
}