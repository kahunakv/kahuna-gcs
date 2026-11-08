# Testing

## Run the tests

1. Start the GCS emulator:

   ```sh
   docker compose up -d
   ```

2. Run the tests:

   ```sh
   dotnet test
   ```

If the emulator is not running, the tests that need it are skipped, not failed.

To make sure that the emulator tests ran, do what CI does:

```sh
dotnet test -- xUnit.FailSkips=true
```

With this option, a skipped test fails the run.

## Server tests

The tests in `TestServerEndToEnd` start the real Kahuna server with the extension bundle, and use
only its REST API. CI extracts the bundle from the package that it packs, so the tests load the files
that a server node downloads. They are the only tests that show these facts:

- The bundle loads in the server, and its Google dependencies resolve next to the server's own
  versions of `Google.Protobuf` and `Grpc.*`.
- A standalone node takes a full and an incremental backup into the bucket. A restore from the bucket
  gives a store that a new node serves the keys from.
- A 3-node cluster takes a coordinated backup and an incremental backup. Every node lists the same
  backups from the bucket. A fourth node seeds its store from the bucket with
  `--pitr-bootstrap-from` and joins.
- A node whose bucket fails the checks does not start and exits with code 1, as a standalone node and
  as a cluster node.

The tests need a published server and a bundle. Without them, they are skipped. A local run can use
the output of `scripts/publish-bundle.sh`, which is the same set of files as the package folder:

```sh
dotnet tool install Kahuna.Server --version <Kahuna.Core version> --tool-path /tmp/kahuna-server
scripts/publish-bundle.sh -o /tmp/gcs-bundle

export KAHUNA_SERVER_DLL="$(find /tmp/kahuna-server/.store -name Kahuna.Server.dll -path '*/tools/*')"
export KAHUNA_GCS_BUNDLE_DLL=/tmp/gcs-bundle/Kahuna.Storage.Gcs.dll
dotnet test --filter "FullyQualifiedName~TestServerEndToEnd"
```

The server is the `Kahuna.Server` tool from NuGet. Its version must be equal to or higher than the
`Kahuna.Core` version that this package references, or the server refuses the bundle. CI installs the
tool at exactly that version, the oldest server that the package supports.

## What the emulator supports

We checked each item below against the emulator image.

| Feature | Emulator | How the tests cover it |
| --- | --- | --- |
| Listing with page tokens | Supported | Emulator tests |
| Range reads | Supported | Emulator tests |
| Resumable uploads | Supported | Emulator tests |
| Per-object deletes | Supported | Emulator tests |
| Generation preconditions | Not supported. A create-if-absent write replaces the object. Real GCS returns HTTP 412. | A scripted transport checks the request and the handling of a 412 response. |
| Access control | Not supported. Credentials, IAM, and bucket policy do nothing. | The emulator tests turn off the bucket checks. Separate tests use buckets that report no uniform access. |

## What the tests do not prove

- **ADC, workload identity, IAM, and bucket policy.** No test authenticates. Only a run against a real
  bucket tests these.
- **The 412 response from real GCS.** The tests check the request and the 412 handling. The GCS
  documentation is the only source for the 412 response.
- **Artifacts of many gigabytes.** The tests check the streaming path byte by byte at a few
  megabytes only. A production checkpoint can be much larger.
- **More than one writer in one bucket.** The package assumes one writer, as the local target does.
- **Retention and backup garbage collection through the server.** The server tests take, list,
  restore and seed from backups. They do not run the retention policy or `v1/backups/gc` against the
  bucket. The Kahuna test suite tests these against an object store.
