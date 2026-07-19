namespace HeatSynQ.Web.Tests.Administration;

public sealed class OperationsReviewRegressionTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Web_host_is_configured_for_windows_service_lifetime()
    {
        var project = Read("src/Web/HeatSynQ.Web.csproj");
        var program = Read("src/Web/Program.cs");

        Assert.Contains("Microsoft.Extensions.Hosting.WindowsServices", project);
        Assert.Contains("AddWindowsService", program);
    }

    [Fact]
    public void Services_are_registered_through_current_release_junction()
    {
        var script = Read("scripts/install-services.ps1");

        Assert.Contains("current\\Web\\HeatSynQ.Web.exe", script);
        Assert.Contains("current\\Worker\\HeatSynQ.Worker.exe", script);
        Assert.Contains("WebServiceCredential", script);
        Assert.Contains("WorkerServiceCredential", script);
        Assert.Contains("-Credential $WebServiceCredential", script);
        Assert.Contains("-Credential $WorkerServiceCredential", script);
        Assert.Contains("StartName", script);
        Assert.Contains("already exists under", script);
    }

    [Fact]
    public void Failed_deployment_keeps_maintenance_mode_and_rolls_back_junction()
    {
        var script = Read("scripts/deploy-release.ps1");

        Assert.Contains("deploymentSucceeded", script);
        Assert.Contains("previousRelease", script);
        Assert.Contains("junctionUpdateStarted", script);
        Assert.Contains("Restore-PreviousRelease", script);
        Assert.Contains("MaintenanceFlagPath", script);
        Assert.Contains("GetFullPath($MaintenanceFlagPath)", script);
    }

    [Fact]
    public void Backup_and_restore_include_managed_file_storage()
    {
        var backup = Read("scripts/backup-platform.ps1");
        var restore = Read("scripts/restore-platform.ps1");

        Assert.Contains("ManagedStoragePath", backup);
        Assert.Contains("managed-files", backup);
        Assert.Contains("Push-Location $stagingRoot", backup);
        Assert.Contains("TargetManagedStoragePath", restore);
        Assert.Contains("managed-files", restore);
        Assert.Contains("managedStorageStaging", restore);
        Assert.Contains("managedStorageBackup", restore);
        Assert.Contains("Restore-ManagedStorageBackup", restore);
        Assert.Contains("preRestoreDump", restore);
        Assert.Contains("Restore-PreRestoreDatabase", restore);
        Assert.Contains("MaintenanceDatabaseUrl", restore);
        Assert.Contains("TargetDatabaseName", restore);
        Assert.Contains("$PgDropDb", restore);
        Assert.Contains("--create", restore);
        Assert.Contains("SELECT current_database();", restore);
        Assert.Contains("does not match the database addressed", restore);
        Assert.Contains("preserveRestoreArtifacts", restore);
    }

    [Fact]
    public void Forwarded_headers_run_before_transport_security_middleware()
    {
        var program = Read("src/Web/Program.cs");

        Assert.True(
            program.IndexOf("app.UseForwardedHeaders()", StringComparison.Ordinal) <
            program.IndexOf("app.UseHsts()", StringComparison.Ordinal));
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "HeatSynQ.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
