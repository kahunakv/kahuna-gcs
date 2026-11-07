# Authentication

## Credentials

The default is Application Default Credentials (ADC). You do not configure anything for ADC. ADC finds
the credentials from the environment:

| Environment | Credentials |
| --- | --- |
| GKE | Workload identity |
| GCE | The metadata server |
| Developer machine | The `gcloud` user credentials |

Use workload identity where it is available.

### Key file

Use `CredentialsPath` only where ADC is not possible. It accepts a service-account key file only.

A key file has two risks:

- It is a long-lived secret on the local disk.
- A general credentials file can describe an external account. Such a file tells the client to get
  tokens from a URL in the file. If an attacker replaces the file on the node, the attacker controls
  authentication. The service-account-only rule stops this.

## Permissions

The identity needs these permissions on the bucket:

| Permission | Kahuna uses it to |
| --- | --- |
| `storage.buckets.get` | Check the bucket at startup |
| `storage.objects.create` | Write manifests and artifacts |
| `storage.objects.get` | Read manifests, verify backups, and restore |
| `storage.objects.list` | List objects, run the orphan sweep, and delete by prefix |
| `storage.objects.delete` | Run retention and garbage collection |

`roles/storage.objectAdmin` gives the four object permissions. It does not give
`storage.buckets.get`. Use one of these options:

- Grant `roles/storage.objectAdmin` and `roles/storage.legacyBucketReader`.
- Grant a custom role with all five permissions.
