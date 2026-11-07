# Behaviour

This page tells how the GCS target acts when an operation fails or conflicts. For the terms, see the
[glossary](glossary.md).

## An upload makes no object until it is complete

GCS makes an object only when it receives the last chunk. If an upload stops, no partial object
stays in the bucket. Thus, Kahuna has no partial objects to clean up. GCS deletes the unfinished
upload session after it expires.

## A manifest write never replaces a manifest

Kahuna writes a manifest only if no object has that name. Each backup ID is a new GUID, so a conflict
means one of these problems:

- Two writers use the same prefix.
- A restored bucket replaced a bucket that was in use.

In both cases, the manifest in the bucket is the more valuable data, so Kahuna keeps it. The local
target is less strict, because it can only replace a file.

To let Kahuna replace a manifest, set `FailOnManifestIdCollision` to `false`.

## Deletes can need more than one run

GCS cannot delete a prefix in one atomic operation. To delete a backup, Kahuna lists the objects and
deletes each one. A failure can stop this part of the way through. The next run deletes the remaining
objects.

## A failed listing is an error, not an empty result

If a listing fails, Kahuna reports the error. It does not report "no objects".

This is important for the orphan sweep. "No manifests" means that no backup owns any artifact. With
that result, the orphan sweep would delete each artifact in the bucket. A temporary outage must never
cause that.

## POSIX hardening is not supported

The local target sets POSIX file permissions on its files. A bucket has no POSIX permissions. Thus,
the store reports `SupportsPosixHardening: false`. The
[bucket requirements](bucket-requirements.md) give the protection in its place.
