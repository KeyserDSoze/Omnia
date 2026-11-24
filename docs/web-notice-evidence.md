# Web build notice evidence (#218)

## Scope

After the existing locked npm ci and Expo Web export in the mobile CI
job, collect SHA-256 and byte length of root-level regular files named
LICENSE/LICENCE/COPYING/NOTICE/COPYRIGHT/UNLICENSE (including extensions)
for every installed locked npm occurrence. Compare the installed
package.json name/version and preserve both original license
declarations. Missing nonoptional packages, inconsistent identities,
duplicate JSON keys, unsafe relative paths, symlinks in selected paths
and invalid Web export structure fail closed. Optional/devOptional
absences are explicit, not assumed platform-specific. Root notice
absence or declaration differences are review candidates, not approvals.

In the same report inventory each regular file in dist by path,
SHA-256 and byte length. Require index.html and at least one JS file.
Run before Android/iOS Metro exports can overwrite dist. This evidence
describes the mobile CI Web build, **not** byte identity with the separate
Pages deployment or actual dependency inclusion in minified JS. No
automatic notice publication, OR branch choice or compatibility opinion.
No notice contents or Web file bytes are uploaded by this tool.

```sh
cd src/mobile
npm ci
npm run build:web
python3 -m unittest discover -s ../../tests/quality -p test_web_notice_evidence.py
python3 ../../tests/quality/web_notice_evidence.py --root . --output /tmp/web-notice-evidence.json
```

The report is deterministic for identical source SHA, lock bytes,
installed metadata/files and dist bytes. Source commit and lock digest
identify evidence inputs. The 14-day artifact omnia-web-notice-evidence
is separate from the site. Review actual text, nested notices,
copyright, source availability, attribution and bundled module
mapping before a release decision. Missing candidates do not imply
missing license rights; installed does not imply distributed.
Native, NuGet publish and OCI artifacts remain separate obligations.

References:
- https://docs.npmjs.com/cli/v11/commands/npm-ci/
- https://docs.expo.dev/router/web/static-rendering/
- https://spdx.org/licenses/
