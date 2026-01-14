using System.Collections.Generic;
using PhotoAtomic.DeepCloner;
using Xunit;

namespace PhotoAtomic.Darc.Test;

public class DeepClonerAdvancedTests
{
    [Fact]
    public void Clone_UsesHelper_ForPrivateSetters()
    {
        var original = new PrivateSetterSample("alice", 42);

        var clone = original.Clone();

        Assert.NotSame(original, clone);
        Assert.Equal("alice", clone!.Name);
        Assert.Equal(42, clone.Age);
    }

    [Fact]
    public void Clone_SkipsUnsettableProperty_WithWarningBehavior()
    {
        var original = new UnsettableSample("value");

        var clone = original.Clone();

        Assert.NotSame(original, clone);
        Assert.Null(clone!.Name);
    }

    [Fact]
    public void Clone_DispatchesDerivedForBaseProperty()
    {
        var original = new AnimalHolder
        {
            Animal = new Cat { Lives = 9 }
        };

        var clone = original.Clone();

        Assert.NotSame(original, clone);
        var clonedAnimal = Assert.IsType<Cat>(clone!.Animal);
        Assert.Equal(9, clonedAnimal.Lives);
    }

    [Fact]
    public void Clone_UncloneableCollectionElement_Throws()
    {
        var original = new UncloneableCollection
        {
            Items = new List<object>
            {
                new object()
            }
        };

        Assert.Throws<InvalidOperationException>(() => original.Clone());
    }
}

[DeepCopyable]
public partial class PrivateSetterSample
{
    public string Name { get; private set; }
    public int Age { get; private set; }

    public PrivateSetterSample()
    {
        Name = string.Empty;
    }

    public PrivateSetterSample(string name, int age)
    {
        Name = name;
        Age = age;
    }
}

[DeepCopyable]
public class UnsettableSample
{
    public string Name { get; }

    public UnsettableSample()
    {
    }

    public UnsettableSample(string name)
    {
        Name = name;
    }
}

[DeepCopyable]
public abstract class Animal
{
}

[DeepCopyable]
public class Cat : Animal
{
    public int Lives { get; set; }
}

[DeepCopyable]
public class AnimalHolder
{
    public Animal? Animal { get; set; }
}

public class BadElement
{
    public int Value { get; set; }
}

[DeepCopyable]
public class UncloneableCollection
{
    public List<object> Items { get; set; } = new();
}
