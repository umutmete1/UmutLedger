namespace UmutLedger.Domain.Exceptions;

public abstract class LedgerException : Exception
{
    protected LedgerException(ErrorKind kind, string code, string message) : base(message)
    {
        Kind = kind;
        Code = code;
    }

    public ErrorKind Kind { get; }

    public string Code { get; }
}