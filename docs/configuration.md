# Configuration

The Kahuna server reads the settings from the `gcs.*` keys of `--extension-option`. An embedding host
sets the properties of `GcsBackupStorageOptions`. Each key and its property have the same meaning.

| Key | Property | Default | Meaning |
| --- | --- | --- | --- |
| `gcs.bucket` | `Bucket` | *(required)* | The bucket for manifests and artifacts. |
| `gcs.prefix` | `Prefix` | `""` | The start of each object name. An empty value is the bucket root. See [Bucket layout](bucket-layout.md). |
| `gcs.credentials-path` | `CredentialsPath` | `null` | A service-account key file. `null` uses ADC. See [Authentication](authentication.md). |
| `gcs.resumable-upload-threshold-bytes` | `ResumableUploadThresholdBytes` | 8 MiB | An artifact larger than this value streams. A smaller artifact is first read into memory. |
| `gcs.resumable-chunk-size-bytes` | `ResumableChunkSizeBytes` | 8 MiB | The chunk size for an artifact that streams. Kahuna rounds it up to a multiple of 256 KiB. |
| `gcs.require-uniform-bucket-level-access` | `RequireUniformBucketLevelAccess` | `true` | Refuse to start without uniform bucket-level access. See [Bucket requirements](bucket-requirements.md). |
| `gcs.require-public-access-prevention` | `RequirePublicAccessPrevention` | `true` | Refuse to start without public access prevention. See [Bucket requirements](bucket-requirements.md). |
| `gcs.deletion-policy-reaction` | `DeletionPolicyReaction` | `warn` | `warn`, `fail` or `ignore`: what to do when the bucket has versioning, soft delete, or a retention policy. See [Bucket requirements](bucket-requirements.md). |
| `gcs.fail-on-manifest-id-collision` | `FailOnManifestIdCollision` | `true` | Write a manifest only if no object has its name. See [Behaviour](behaviour.md). |
| `gcs.emulator-base-uri` | `EmulatorBaseUri` | `null` | The address of a GCS emulator. **For tests only.** With an address, the client sends no credentials. |

A value for a `true`/`false` key must be `true` or `false`. A value for a byte count must be a whole
number. An error message names the key and never the value, because a value can be a path or a bucket
name.

## About `ResumableUploadThresholdBytes`

Each upload uses the GCS resumable protocol, because the .NET client has no single-request upload.
Thus, this option does not select a protocol. It selects how much memory an upload uses:

- Below the threshold, Kahuna reads the artifact into memory and sends it in one piece.
- Above the threshold, Kahuna streams the bytes. The full artifact is never in memory.
