# Windows single-server deployment

## Target

One supported Windows Server host runs PostgreSQL, HeatSynQ Web, and the HeatSynQ background worker. Encrypted backup archives are copied to a separate protected device.

## Provisioning

1. Patch Windows and restrict interactive administrator access.
2. Install the current supported PostgreSQL release and .NET 10 Hosting Bundle.
3. Create dedicated Windows service identities with log-on-as-service rights and no interactive login.
4. Create a PostgreSQL database and least-privilege application login.
5. Store the connection string outside the repository as `ConnectionStrings__Platform`.
6. Restore repository-local tools with `dotnet tool restore`.
7. Apply migrations using `dotnet tool run dotnet-ef database update`.
8. Publish with `dotnet publish src\Web -c Release -o C:\HeatSynQ\Web`.
9. Bind the web host to an internal DNS name with a company-trusted TLS certificate.
10. Restrict inbound firewall access to approved LAN ranges and the HTTPS port.
11. Configure the Windows service recovery policy to restart after failure.
12. Verify `/health`, application logs, storage permissions, and backup destination access.

## Configuration

Development, test, and production use separate databases and storage roots. Production secrets must be supplied through protected machine-level configuration. Do not grant the web application PostgreSQL superuser rights.

## Update and rollback

1. Announce maintenance mode and drain background work.
2. Record application version and migration state.
3. Run and verify a pre-update backup.
4. Stop HeatSynQ services.
5. Deploy the versioned release to a new directory.
6. Apply forward migrations.
7. Start services and verify health plus smoke tests.
8. Switch the stable service path to the new release.

If verification fails, stop services, restore the previous application directory, and follow the tested database rollback/restore decision documented in the release notes. Never attempt an unreviewed down-migration against production.
