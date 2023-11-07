namespace EastwardLib.Exceptions;

public class ArchiveNotFoundException : Exception
{
    public ArchiveNotFoundException(string message) : base(message)
    {
    }
}