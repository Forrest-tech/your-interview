namespace YourInterview.BuildingBlocks.Domain;

/// <summary>值对象基类:按结构判等,不可变。</summary>
public abstract class ValueObject
{
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != GetType()) return false;
        return GetEqualityComponents().SequenceEqual(((ValueObject)obj).GetEqualityComponents());
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var c in GetEqualityComponents()) hash.Add(c);
        return hash.ToHashCode();
    }

    public static bool operator ==(ValueObject? a, ValueObject? b) => Equals(a, b);
    public static bool operator !=(ValueObject? a, ValueObject? b) => !Equals(a, b);
}
