# Configuration

These are the properties of `GcsBackupStorageOptions`.

| Option | Default | Meaning |
| --- | --- | --- |
| `Bucket` | *(required)* | The bucket for manifests and artifacts. |
| `Prefix` | `""` | The start of each object name. An empty value is the bucket root. See [Bucket layout](bucket-layout.md). |
| `CredentialsPath` | `null` | A service-account key file. `null` uses ADC. See [Authentication](authentication.md). |
| `ResumableUploadThresholdBytes` | 8 MiB | An artifact larger than this value streams. A smaller artifact is first read into memory. |
| `ResumableChunkSizeBytes` | 8 MiB | The chunk size for an artifact that streams. Kahuna rounds it up to a multiple of 256 KiB. |
| `RequireUniformBucketLevelAccess` | `true` | Refuse to start without uniform bucket-level access. See [Bucket requirements](bucket-requirements.md). |
| `RequirePublicAccessPrevention` | `true` | Refuse to start without public access prevention. See [Bucket requirements](bucket-requirements.md). |
| `DeletionPolicyReaction` | `Warn` | What to do when the bucket has versioning, soft delete, or a retention policy. See [Bucket requirements](bucket-requirements.md). |
| `FailOnManifestIdCollision` | `true` | Write a manifest only if no object has its name. See [Behaviour](behaviour.md). |
| `EmulatorBaseUri` | `null` | The address of a GCS-compatible endpoint, for tests. |

## About `ResumableUploadThresholdBytes`

Each upload uses the GCS resumable protocol, because the .NET client has no single-request upload.
Thus, this option does not select a protocol. It selects how much memory an upload uses:

- Below the threshold, Kahuna reads the artifact into memory and sends it in one piece.
- Above the threshold, Kahuna streams the bytes. The full artifact is never in memory.
