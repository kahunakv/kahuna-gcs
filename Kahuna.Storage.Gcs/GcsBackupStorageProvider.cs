using Kahuna.Server.Persistence.Pitr;

namespace Kahuna.Storage.Gcs;

/// <summary>
/// The Google Cloud Storage backup target, <c>--pitr-backup-target gcs</c>.
/// <para>
/// A server loads it with <c>--extension-assembly</c> and the constructor without parameters. It then reads
/// its settings from the <c>gcs.*</c> keys of <c>--extension-option</c> (see <see cref="GcsExtensionOptions"/>).
/// An embedding host builds it with <see cref="GcsBackupStorageProvider(GcsBackupStorageOptions)"/> and
/// registers it in <c>EmbeddedKahunaOptions.StorageProviders</c>.
/// </para>
/// <para>
/// <see cref="Open"/> only reads and checks the settings. The node calls <see cref="IBackupStorage.StartAsync"/>
/// before it joins its cluster: that call resolves the credentials and checks the bucket, so a node whose
/// backup destination is unreachable or unprotected does not start.
/// </para>
/// </summary>
public sealed class GcsBackupStorageProvider : IBackupStorageProvider
{
    /// <summary>The target name, the value of <c>--pitr-backup-target</c> that selects this target.</summary>
    public const string Name = "gcs";

    private readonly GcsBackupStorageOptions? options;

    /// <summary>For the extension loader: the settings come from the <c>gcs.*</c> extension options.</summary>
    public GcsBackupStorageProvider()
    {
    }

    /// <summary>
    /// For an embedding host: the settings are <paramref name="options"/>. The node then refuses any
    /// <c>gcs.*</c> extension option, so the settings have one source.
    /// </summary>
    public GcsBackupStorageProvider(GcsBackupStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <inheritdoc />
    public string TargetName => Name;

    /// <summary>
    /// True: a persistence backend writes a checkpoint only through the filesystem, so every checkpoint passes
    /// through the scratch directory before its upload.
    /// </summary>
    public bool RequiresLocalScratch => true;

    /// <inheritdoc />
    public IBackupStorage Open(BackupStorageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        GcsBackupStorageOptions effective;

        if (options is null)
        {
            effective = GcsExtensionOptions.Parse(context.ExtensionOptions);
        }
        else
        {
            GcsExtensionOptions.RefuseAny(context.ExtensionOptions);
            effective = options;
        }

        effective.Validate();

        // Kahuna validates the scratch directory at startup, but a host can build a configuration without
        // that path, and the failure it would otherwise get is an unhelpful one deep inside a checkpoint write.
        if (string.IsNullOrWhiteSpace(context.ScratchDir))
            throw new KahunaServerException(
                "The Google Cloud Storage backup target stages every checkpoint through a local directory, " +
                "because a persistence backend can only write one through the filesystem. Configure a backup " +
                "scratch directory and size it for one full backup, which transits it in its entirety.");

        return new GcsBackupStorage(effective, context.ScratchDir, context.Logger);
    }
}
