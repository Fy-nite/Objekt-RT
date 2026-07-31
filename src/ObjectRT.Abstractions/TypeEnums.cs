namespace ObjectRT.Abstractions;

public enum TypeKind : byte
{
    Class     = 0x01,
    Interface = 0x02,
    Struct    = 0x03,
    Enum      = 0x04,
}

public static class TypeKindExtensions
{
    public static string ToDisplayString(this TypeKind k) => k switch
    {
        TypeKind.Class     => "class",
        TypeKind.Interface => "interface",
        TypeKind.Struct    => "struct",
        TypeKind.Enum      => "enum",
        _                  => "unknown",
    };
}

[Flags]
public enum TypeFlags : byte
{
    None     = 0x00,
    Abstract = 0x01,
    Sealed   = 0x02,
}

public enum MemberAccess : byte
{
    Public    = 0x01,
    Private   = 0x02,
    Protected = 0x03,
    Internal  = 0x04,
}

public static class MemberAccessExtensions
{
    public static string ToDisplayString(this MemberAccess a) => a switch
    {
        MemberAccess.Public    => "public",
        MemberAccess.Private   => "private",
        MemberAccess.Protected => "protected",
        MemberAccess.Internal  => "internal",
        _                      => "unknown",
    };
}

[Flags]
public enum MethodFlags : byte
{
    None     = 0x00,
    Static   = 0x01,
    Virtual  = 0x02,
    Override = 0x04,
    Abstract = 0x08,
}

public enum ImportKind : byte
{
    Type   = 0x01,
    Method = 0x02,
    Field  = 0x03,
}

public enum MetadataValueKind : byte
{
    String     = 0x01,
    StringList = 0x02,
}
