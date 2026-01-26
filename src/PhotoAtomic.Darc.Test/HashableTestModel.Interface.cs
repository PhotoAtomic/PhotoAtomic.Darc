using PhotoAtomic.Clooney;

namespace PhotoAtomic.Darc.Test;

public partial class HashableTestModel : IHashable
{
    public int HashValue()
    {
        return HashableTestModelHashExtensions.HashValue(this);
    }
}
