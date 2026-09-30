using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Security;

public sealed class DataProtectionSecretProtectorTests : IDisposable
{
    private readonly string keyDirectory = Path.Combine(Path.GetTempPath(), $"cf-keys-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(keyDirectory))
        {
            Directory.Delete(keyDirectory, recursive: true);
        }
    }

    [Fact]
    public void Protected_value_round_trips_and_hides_the_plaintext()
    {
        using var services = Provider();
        var protector = Protector(services);

        var protectedValue = protector.Protect(SecretPurpose.RegistryPassword, "hunter2");

        Assert.DoesNotContain("hunter2", protectedValue);
        Assert.Equal("hunter2", protector.Unprotect(SecretPurpose.RegistryPassword, protectedValue));
    }

    [Fact]
    public void Values_stay_readable_after_a_restart_with_the_same_key_directory()
    {
        string protectedValue;
        using (var first = Provider())
        {
            protectedValue = Protector(first).Protect(SecretPurpose.RegistryPassword, "hunter2");
        }

        using var second = Provider();
        Assert.Equal("hunter2", Protector(second).Unprotect(SecretPurpose.RegistryPassword, protectedValue));
        Assert.NotEmpty(Directory.GetFiles(keyDirectory));
    }

    [Fact]
    public void Tampered_or_foreign_values_are_unreadable()
    {
        using var services = Provider();
        var protector = Protector(services);
        var protectedValue = protector.Protect(SecretPurpose.RegistryPassword, "hunter2");

        Assert.Throws<SecretUnreadableException>(() => protector.Unprotect(SecretPurpose.RegistryPassword, protectedValue[..^4] + "AAAA"));
        Assert.Throws<SecretUnreadableException>(() => protector.Unprotect(SecretPurpose.RegistryPassword, "not protected"));
    }

    [Fact]
    public void Lost_key_ring_makes_values_unreadable()
    {
        string protectedValue;
        using (var first = Provider())
        {
            protectedValue = Protector(first).Protect(SecretPurpose.RegistryPassword, "hunter2");
        }

        Directory.Delete(keyDirectory, recursive: true);

        using var second = Provider();
        Assert.Throws<SecretUnreadableException>(() => Protector(second).Unprotect(SecretPurpose.RegistryPassword, protectedValue));
    }

    private ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("container-forest").PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
        return services.BuildServiceProvider();
    }

    private static DataProtectionSecretProtector Protector(IServiceProvider services) =>
        new(services.GetRequiredService<IDataProtectionProvider>());
}
