using PhotoAtomic.Clooney;

namespace PhotoAtomic.Darc.Test;

// Base classes - WITH attributes to trigger extension method generation
// BUT interface implementations are manual in PolymorphicTestModels.Interfaces.cs
[Clonable]
public partial class VehicleBase
{
    public string? Model { get; set; }
    public int Year { get; set; }
}

[Hashable]
public partial class AnimalBase
{
    public string? Name { get; set; }
    public int Age { get; set; }
}

[Diffable]
public partial class DocumentBase
{
    public string? Title { get; set; }
    public string? Author { get; set; }
}

// Derived classes - WITH attributes
[Clonable]
public partial class CarDerived : VehicleBase
{
    public int Doors { get; set; }
}

[Clonable]
public partial class MotorcycleDerived : VehicleBase
{
    public bool HasSidecar { get; set; }
}

[Hashable]
public partial class DogDerived : AnimalBase
{
    public string? Breed { get; set; }
}

[Hashable]
public partial class CatDerived : AnimalBase
{
    public int Lives { get; set; }
}

[Diffable]
public partial class BookDerived : DocumentBase
{
    public int Pages { get; set; }
}

[Diffable]
public partial class ArticleDerived : DocumentBase
{
    public string? Journal { get; set; }
}

// Second level derived classes
[Clonable]
public partial class SportsCarDerived : CarDerived
{
    public int TopSpeed { get; set; }
}

[Hashable]
public partial class GermanShepherdDerived : DogDerived
{
    public bool IsK9Trained { get; set; }
}

[Diffable]
public partial class TextbookDerived : BookDerived
{
    public string? Subject { get; set; }
}




