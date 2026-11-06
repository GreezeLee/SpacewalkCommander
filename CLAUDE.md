# CLAUDE.md

## Source of truth: `documents/internals.md`

[documents/internals.md](documents/internals.md) describes the internal mechanisms of this application:
architecture, the Bluetooth FTMS protocol, workout recording and analysis, the local archive, the Google Health
integration, settings, conventions and known limitations.

**Before working on this repository**, read `documents/internals.md` and take information from it instead of
re-deriving it from the code. If the document and the code disagree, the code is right: fix the document and
point out the discrepancy to the user.

## Keep the document current

Every change that affects anything described in `documents/internals.md` must update it **in the same change**:

- behaviour, protocols, timeouts, constants, file formats, folder paths, settings keys, API calls or scopes,
- project structure (new or moved files, folders, projects),
- conventions and known limitations.

Also add a line to the **Change history** table at the end of the document (date, short description).
The document is written in English. A change is not complete until the document is updated.
