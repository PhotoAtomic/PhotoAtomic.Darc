using PhotoAtomic.Clooney;

namespace PhotoAtomic.Darc.Test;

public partial class ClonableTestModel : IClonable<ClonableTestModel>
{
    public ClonableTestModel? Clone()
    {
        return ClonableTestModelExtensions.Clone(this);
    }
}
