using PhotoAtomic.Clooney;
using System.Collections.Generic;

namespace PhotoAtomic.Darc.Test;

public partial class DiffableTestModel : IDifferentiable<DiffableTestModel>
{
    public IEnumerable<DifferencePath> Diff(DiffableTestModel? other)
    {
        return DiffableTestModelDiffExtensions.Diff(this, other);
    }
}
