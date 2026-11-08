using System.Globalization;

namespace Kahuna.Storage.Gcs;

/// <summary>
/// Reads <see cref="GcsBackupStorageOptions"/> from the <c>gcs.*</c> keys of <c>--extension-option</c>.
/// <para>
/// A key that starts with <c>gcs.</c> and is not in the list below is refused, so a typo cannot turn off
/// a bucket protection without a message. A message names the key, never the value: a value can be a path
/// or a bucket name that does not belong in a log. Keys of other extensions are ignored.
/// </para>
/// </summary>
public static class GcsExtensionOptions
{
    /// <summary>The prefix of every key this target reads.</summary>
    public const string KeyPrefix = GcsBackupStorageProvider.Name + ".";

    /// <summary><see cref="GcsBackupStorageOptions.Bucket"/>. Required.</summary>
    public const string Bucket = KeyPrefix + "bucket";

    /// <summary><see cref="GcsBackupStorageOptions.Prefix"/>.</summary>
    public const string Prefix = KeyPrefix + "prefix";

    /// <summary><see cref="GcsBackupStorageOptions.CredentialsPath"/>.</summary>
    public const string CredentialsPath = KeyPrefix + "credentials-path";

    /// <summary><see cref="GcsBackupStorageOptions.RequireUniformBucketLevelAccess"/>: <c>true</c> or <c>false</c>.</summary>
    public const string RequireUniformBucketLevelAccess = KeyPrefix + "require-uniform-bucket-level-access";

    /// <summary><see cref="GcsBackupStorageOptions.RequirePublicAccessPrevention"/>: <c>true</c> or <c>false</c>.</summary>
    public const string RequirePublicAccessPrevention = KeyPrefix + "require-public-access-prevention";

    /// <summary><see cref="GcsBackupStorageOptions.DeletionPolicyReaction"/>: <c>warn</c>, <c>fail</c> or <c>ignore</c>.</summary>
    public const string DeletionPolicyReaction = KeyPrefix + "deletion-policy-reaction";

    /// <summary><see cref="GcsBackupStorageOptions.FailOnManifestIdCollision"/>: <c>true</c> or <c>false</c>.</summary>
    public const string FailOnManifestIdCollision = KeyPrefix + "fail-on-manifest-id-collision";

    /// <summary><see cref="GcsBackupStorageOptions.ResumableUploadThresholdBytes"/>: a whole number of bytes.</summary>
    public const string ResumableUploadThresholdBytes = KeyPrefix + "resumable-upload-threshold-bytes";

    /// <summary><see cref="GcsBackupStorageOptions.ResumableChunkSizeBytes"/>: a whole number of bytes.</summary>
    public const string ResumableChunkSizeBytes = KeyPrefix + "resumable-chunk-size-bytes";

    /// <summary>
    /// <see cref="GcsBackupStorageOptions.EmulatorBaseUri"/>: the address of a GCS emulator. For tests only. With
    /// it, the client sends no credentials, and an emulator checks no access control.
    /// </summary>
    public const string EmulatorBaseUri = KeyPrefix + "emulator-base-uri";

    /// <summary>Builds the options from <paramref name="values"/>; throws <see cref="KahunaServerException"/> naming a bad key.</summary>
    public static GcsBackupStorageOptions Parse(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        GcsBackupStorageOptions options = new();

        foreach ((string key, string value) in values)
        {
            if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                continue;

            switch (key)
            {
                case Bucket:
                    options.Bucket = value;
                    break;

                case Prefix:
                    options.Prefix = value;
                    break;

                case CredentialsPath:
                    options.CredentialsPath = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;

                case RequireUniformBucketLevelAccess:
                    options.RequireUniformBucketLevelAccess = ParseBool(key, value);
                    break;

                case RequirePublicAccessPrevention:
                    options.RequirePublicAccessPrevention = ParseBool(key, value);
                    break;

                case DeletionPolicyReaction:
                    options.DeletionPolicyReaction = ParseReaction(key, value);
                    break;

                case FailOnManifestIdCollision:
                    options.FailOnManifestIdCollision = ParseBool(key, value);
                    break;

                case ResumableUploadThresholdBytes:
                    options.ResumableUploadThresholdBytes = ParseInt(key, value);
                    break;

                case ResumableChunkSizeBytes:
                    options.ResumableChunkSizeBytes = ParseInt(key, value);
                    break;

                case EmulatorBaseUri:
                    options.EmulatorBaseUri = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;

                default:
                    throw new KahunaServerException(
                        $"--extension-option '{key}' is not a setting of the Google Cloud Storage backup target. " +
                        $"Its settings are {string.Join(", ", AllKeys)}.");
            }
        }

        return options;
    }

    /// <summary>
    /// Refuses every <c>gcs.*</c> key, for a provider that an embedding host built with its own options: two
    /// sources for one setting would leave the operator to guess which one applies.
    /// </summary>
    public static void RefuseAny(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (string key in values.Keys)
            if (key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                throw new KahunaServerException(
                    $"--extension-option '{key}' is given, but this Google Cloud Storage backup target was built " +
                    "with its own options. Set the value in those options instead.");
    }

    private static readonly string[] AllKeys =
    [
        Bucket, Prefix, CredentialsPath, RequireUniformBucketLevelAccess, RequirePublicAccessPrevention,
        DeletionPolicyReaction, FailOnManifestIdCollision, ResumableUploadThresholdBytes, ResumableChunkSizeBytes,
        EmulatorBaseUri,
    ];

    private static bool ParseBool(string key, string value) =>
        bool.TryParse(value, out bool parsed)
            ? parsed
            : throw new KahunaServerException($"--extension-option '{key}' must be true or false.");

    private static int ParseInt(string key, string value) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new KahunaServerException($"--extension-option '{key}' must be a whole number of bytes.");

    private static GcsDeletionPolicyReaction ParseReaction(string key, string value)
    {
        if (string.Equals(value, "warn", StringComparison.OrdinalIgnoreCase))
            return GcsDeletionPolicyReaction.Warn;

        if (string.Equals(value, "fail", StringComparison.OrdinalIgnoreCase))
            return GcsDeletionPolicyReaction.Fail;

        if (string.Equals(value, "ignore", StringComparison.OrdinalIgnoreCase))
            return GcsDeletionPolicyReaction.Ignore;

        throw new KahunaServerException($"--extension-option '{key}' must be warn, fail or ignore.");
    }
}
