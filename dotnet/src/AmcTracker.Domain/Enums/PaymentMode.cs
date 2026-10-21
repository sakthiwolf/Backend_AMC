namespace AmcTracker.Domain.Enums;

/// <summary>How a renewal payment was received. Stored and serialised by name.</summary>
public enum PaymentMode
{
    Cash,
    UPI,
    Bank,
}
