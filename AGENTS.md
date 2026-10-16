# WinMovie project workflow

- The maintainer uses the `v1.0-beta.N` sequence. The original build was beta 1, the filmstrip and drag-and-drop build is beta 2, and the next feature iteration is beta 3. Do not call a build a release candidate or stable version unless the maintainer requests that change.
- After completing a change set, build and run the relevant smoke test, commit the changed source files, and push the commit to `origin/main`. Verify the remote branch contains the commit. Never force-push or silently discard remote work.
- For a new beta build, prepare a Windows x64 ZIP from the complete publish folder, including `WinMovie.pri`. Create or update a **draft** GitHub release with its matching beta tag, release notes, and ZIP. Leave the release unpublished until the maintainer explicitly asks to publish it.
- Keep generated `bin/`, `obj/`, videos, and ZIP archives out of Git. Attach distributable ZIPs to GitHub Releases rather than committing them to the repository.
