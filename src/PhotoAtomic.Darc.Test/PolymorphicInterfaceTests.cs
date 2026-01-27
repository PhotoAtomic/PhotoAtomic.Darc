using PhotoAtomic.Clooney;
using System.Linq;
using Xunit;

namespace PhotoAtomic.Darc.Test;

/// <summary>
/// Tests to verify that interface methods on partial base classes correctly dispatch
/// to the extension methods of the most derived type at runtime.
/// </summary>
public class PolymorphicInterfaceTests
{
    #region IClonable<T> Polymorphic Tests

    [Fact]
    public void ClonableBase_WithDerivedInstance_CallsDerivedExtensionMethod()
    {
        // Arrange - Create a derived instance but store as base type
        VehicleBase vehicle = new CarDerived
        {
            Model = "Sedan",
            Year = 2024,
            Doors = 4
        };

        // Act - Call Clone() through the base interface (the base type implements IClonable)
        var cloned = vehicle.Clone();

        // Assert - Should clone as the derived type
        Assert.NotNull(cloned);
        Assert.IsType<CarDerived>(cloned);
        var clonedCar = (CarDerived)cloned!;
        Assert.Equal("Sedan", clonedCar.Model);
        Assert.Equal(2024, clonedCar.Year);
        Assert.Equal(4, clonedCar.Doors);
    }

    [Fact]
    public void ClonableBase_WithMultipleDerivedTypes_DispatchesCorrectly()
    {
        // Arrange - Create different derived types
        VehicleBase car = new CarDerived { Model = "Car", Year = 2024, Doors = 4 };
        VehicleBase motorcycle = new MotorcycleDerived { Model = "Bike", Year = 2023, HasSidecar = true };

        // Act - Clone through base interface
        var clonedCar = car.Clone();
        var clonedMotorcycle = motorcycle.Clone();

        // Assert - Each should be cloned as its actual type
        Assert.IsType<CarDerived>(clonedCar);
        Assert.IsType<MotorcycleDerived>(clonedMotorcycle);
        
        Assert.Equal(4, ((CarDerived)clonedCar!).Doors);
        Assert.True(((MotorcycleDerived)clonedMotorcycle!).HasSidecar);
    }

    [Fact]
    public void ClonableBase_WithDeeplyDerivedType_CallsMostSpecificExtension()
    {
        // Arrange - Create a deeply derived instance (3 levels)
        VehicleBase sportsCar = new SportsCarDerived
        {
            Model = "Ferrari",
            Year = 2024,
            Doors = 2,
            TopSpeed = 320
        };

        // Act - Clone through base interface
        var cloned = sportsCar.Clone();

        // Assert - Should clone as the most specific type
        Assert.NotNull(cloned);
        Assert.IsType<SportsCarDerived>(cloned);
        var clonedSportsCar = (SportsCarDerived)cloned!;
        Assert.Equal("Ferrari", clonedSportsCar.Model);
        Assert.Equal(320, clonedSportsCar.TopSpeed);
    }

    [Fact]
    public void ClonableBase_WithBaseInstanceOnly_ClonesAsBase()
    {
        // Arrange - Create an instance of the base type itself
        VehicleBase vehicle = new VehicleBase
        {
            Model = "Generic",
            Year = 2020
        };

        // Act - Clone through interface
        var cloned = vehicle.Clone();

        // Assert - Should clone as base type
        Assert.NotNull(cloned);
        Assert.Equal(typeof(VehicleBase), cloned!.GetType());
        Assert.Equal("Generic", cloned.Model);
        Assert.Equal(2020, cloned.Year);
    }

    #endregion

    #region IHashable Polymorphic Tests

    [Fact]
    public void HashableBase_WithDerivedInstance_CallsDerivedExtensionMethod()
    {
        // Arrange - Create a derived instance but store as base type
        AnimalBase animal = new DogDerived
        {
            Name = "Buddy",
            Age = 5,
            Breed = "Golden Retriever"
        };

        // Act - Compute hash through the base interface
        var hash = animal.HashValue();

        // Assert - Should use derived type's hash
        Assert.NotEqual(0, hash);
        
        // Verify consistency - same instance should produce same hash
        var hash2 = animal.HashValue();
        Assert.Equal(hash, hash2);
    }

    [Fact]
    public void HashableBase_WithDifferentDerivedTypes_ProducesDifferentHashes()
    {
        // Arrange - Create two different derived types with same base values
        AnimalBase dog = new DogDerived { Name = "Pet", Age = 5, Breed = "Labrador" };
        AnimalBase cat = new CatDerived { Name = "Pet", Age = 5, Lives = 9 };

        // Act - Compute hashes through base interface
        var dogHash = dog.HashValue();
        var catHash = cat.HashValue();

        // Assert - Should produce different hashes due to different derived properties
        Assert.NotEqual(dogHash, catHash);
    }

    [Fact]
    public void HashableBase_WithDeeplyDerivedType_UsesMostSpecificHash()
    {
        // Arrange - Create a deeply derived instance
        AnimalBase germanShepherd = new GermanShepherdDerived
        {
            Name = "Rex",
            Age = 3,
            Breed = "German Shepherd",
            IsK9Trained = true
        };

        // Act - Compute hash through base interface
        var hash = germanShepherd.HashValue();

        // Assert - Should use the most specific type's hash
        Assert.NotEqual(0, hash);
        
        // Verify that the deeply derived property affects the hash
        var germanShepherd2 = new GermanShepherdDerived
        {
            Name = "Rex",
            Age = 3,
            Breed = "German Shepherd",
            IsK9Trained = false  // Different value
        };
        var hash2 = germanShepherd2.HashValue();
        Assert.NotEqual(hash, hash2);
    }

    [Fact]
    public void HashableBase_SameValuesInDerivedInstances_ProduceSameHash()
    {
        // Arrange - Create two instances of the same derived type with identical values
        AnimalBase dog1 = new DogDerived { Name = "Buddy", Age = 5, Breed = "Labrador" };
        AnimalBase dog2 = new DogDerived { Name = "Buddy", Age = 5, Breed = "Labrador" };

        // Act
        var hash1 = dog1.HashValue();
        var hash2 = dog2.HashValue();

        // Assert - Same values should produce same hash
        Assert.Equal(hash1, hash2);
    }

    #endregion

    #region IDifferentiable<T> Polymorphic Tests

    [Fact]
    public void DiffableBase_WithDerivedInstance_CallsDerivedExtensionMethod()
    {
        // Arrange - Create derived instances
        DocumentBase doc1 = new BookDerived
        {
            Title = "C# Guide",
            Author = "John Doe",
            Pages = 500
        };

        DocumentBase doc2 = new BookDerived
        {
            Title = "C# Guide",
            Author = "Jane Smith",  // Different
            Pages = 500
        };

        // Act - Diff through base interface
        var differences = doc1.Diff(doc2);

        // Assert - Should detect differences using derived type's diff
        var diffList = differences.ToList();
        Assert.NotEmpty(diffList);
        Assert.Contains(diffList, d => d.ToString().Contains("Author"));
    }

    [Fact]
    public void DiffableBase_WithDifferentDerivedTypes_DetectsDifferences()
    {
        // Arrange - Create two different derived types
        DocumentBase book = new BookDerived { Title = "Book", Author = "Author", Pages = 300 };
        DocumentBase article = new ArticleDerived { Title = "Book", Author = "Author", Journal = "Science" };

        // Act - Diff through base interface
        var differences = book.Diff(article);

        // Assert - Should detect type difference or property differences
        var diffList = differences.ToList();
        Assert.NotEmpty(diffList);
    }

    [Fact]
    public void DiffableBase_WithDeeplyDerivedType_UsesMostSpecificDiff()
    {
        // Arrange - Create deeply derived instances
        DocumentBase textbook1 = new TextbookDerived
        {
            Title = "Math 101",
            Author = "Prof. Smith",
            Pages = 400,
            Subject = "Mathematics"
        };

        DocumentBase textbook2 = new TextbookDerived
        {
            Title = "Math 101",
            Author = "Prof. Smith",
            Pages = 400,
            Subject = "Physics"  // Different
        };

        // Act - Diff through base interface
        var differences = textbook1.Diff(textbook2);

        // Assert - Should detect the difference in the deeply derived property
        var diffList = differences.ToList();
        Assert.NotEmpty(diffList);
        Assert.Contains(diffList, d => d.ToString().Contains("Subject"));
    }

    [Fact]
    public void DiffableBase_IdenticalDerivedInstances_ProducesNoDifferences()
    {
        // Arrange - Create two identical derived instances
        DocumentBase book1 = new BookDerived { Title = "Title", Author = "Author", Pages = 200 };
        DocumentBase book2 = new BookDerived { Title = "Title", Author = "Author", Pages = 200 };

        // Act
        var differences = book1.Diff(book2);

        // Assert - Should detect no differences
        Assert.Empty(differences);
    }

    [Fact]
    public void DiffableBase_WithCollectionOfDerivedTypes_DiffsCorrectly()
    {
        // Arrange - Create a collection of different derived types
        var documents = new DocumentBase[]
        {
            new BookDerived { Title = "Book1", Author = "A1", Pages = 100 },
            new ArticleDerived { Title = "Article1", Author = "A2", Journal = "J1" },
            new TextbookDerived { Title = "Text1", Author = "A3", Pages = 200, Subject = "Math" }
        };

        var baseline = new BookDerived { Title = "Book1", Author = "A1", Pages = 100 };

        // Act - Diff each against baseline
        var results = documents.Select(d => d.Diff(baseline).Any()).ToArray();

        // Assert
        Assert.False(results[0]);  // Identical to baseline
        Assert.True(results[1]);   // Different type/properties
        Assert.True(results[2]);   // Different type/properties
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public void PolymorphicInterfaces_WithNullCheck_HandlesGracefully()
    {
        // Arrange
        VehicleBase? vehicle = null;

        // Act & Assert - Should not throw when cloning null through extension method
        var cloned = VehicleBaseExtensions.Clone(vehicle);
        Assert.Null(cloned);
    }

    [Fact]
    public void PolymorphicInterfaces_MixedHierarchy_AllWorkTogether()
    {
        // Arrange - Create instances at different levels
        VehicleBase baseVehicle = new VehicleBase { Model = "Base", Year = 2020 };
        VehicleBase derivedCar = new CarDerived { Model = "Car", Year = 2021, Doors = 4 };
        VehicleBase deepDerived = new SportsCarDerived { Model = "Sports", Year = 2024, Doors = 2, TopSpeed = 300 };

        // Act - Clone all through the same method
        var cloned1 = baseVehicle.Clone();
        var cloned2 = derivedCar.Clone();
        var cloned3 = deepDerived.Clone();

        // Assert - Each should be cloned with correct type
        Assert.Equal(typeof(VehicleBase), cloned1!.GetType());
        Assert.Equal(typeof(CarDerived), cloned2!.GetType());
        Assert.Equal(typeof(SportsCarDerived), cloned3!.GetType());
    }

    #endregion
}
