using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class DeviceModelStoreTests
{
    [Fact]
    public async Task SavesAndRestoresModelBySerial()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var store = new DeviceModelStore(paths);
            await store.InitializeAsync();

            Assert.Null(await store.GetModelAsync("SERIAL-0001"));

            await store.SetModelAsync("SERIAL-0001", "Kindle Paperwhite 11 代");
            Assert.Equal("Kindle Paperwhite 11 代", await store.GetModelAsync("SERIAL-0001"));

            // A fresh store instance reads the same database.
            var reopened = new DeviceModelStore(paths);
            await reopened.InitializeAsync();
            Assert.Equal("Kindle Paperwhite 11 代", await reopened.GetModelAsync("serial-0001"));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task UpdatingModelForSameSerialOverwritesMapping()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var store = new DeviceModelStore(paths);
            await store.InitializeAsync();
            await store.SetModelAsync("A1B2C3D4", "Kindle Oasis");

            await store.SetModelAsync("A1B2C3D4", "掌阅 iReader Smart 3");

            Assert.Equal("掌阅 iReader Smart 3", await store.GetModelAsync("A1B2C3D4"));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task DifferentSerialsKeepIndependentModels()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var store = new DeviceModelStore(paths);
            await store.InitializeAsync();
            await store.SetModelAsync("SERIAL-A", "汉王 N10");
            await store.SetModelAsync("SERIAL-B", "Kobo Libra 2");

            Assert.Equal("汉王 N10", await store.GetModelAsync("SERIAL-A"));
            Assert.Equal("Kobo Libra 2", await store.GetModelAsync("SERIAL-B"));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task DeletingModelFallsBackToDefault()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var store = new DeviceModelStore(paths);
            await store.InitializeAsync();
            await store.SetModelAsync("SERIAL-X", "Kindle Scribe");

            await store.DeleteModelAsync("SERIAL-X");

            Assert.Null(await store.GetModelAsync("SERIAL-X"));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public void CatalogCoversExpectedVendorsWithModels()
    {
        var vendors = DeviceModelCatalog.Vendors;

        Assert.Contains(vendors, vendor => vendor.Name == "Kindle");
        Assert.Contains(vendors, vendor => vendor.Name == "汉王");
        Assert.Contains(vendors, vendor => vendor.Name == "掌阅");
        Assert.Contains(vendors, vendor => vendor.Name == "Kobo");
        Assert.All(vendors, vendor => Assert.NotEmpty(vendor.Models));
        Assert.All(
            vendors.SelectMany(vendor => vendor.Models),
            model => Assert.False(string.IsNullOrWhiteSpace(model)));
    }
}
