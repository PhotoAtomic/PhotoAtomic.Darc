using PhotoAtomic.Clooney;

namespace PhotoAtomic.Darc.Test;

[Clonable]
public partial class ClonableTestModel
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public double Value { get; set; }
}

[Hashable]
public partial class HashableTestModel
{
    public string? Key { get; set; }
    public int Counter { get; set; }
    public bool Flag { get; set; }
}

[Diffable]
public partial class DiffableTestModel
{
    public string? Name { get; set; }
    public int Version { get; set; }
    public bool Active { get; set; }
}
