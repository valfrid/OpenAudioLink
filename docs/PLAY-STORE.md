# Releasing the phone app on Google Play

The plan, and the things about *this* app that a generic Play plan does
not cover. The shape is the existing one — GitHub is authoritative,
Actions builds and tests, a tag produces a release — with an App Bundle
and a store listing added on the end.

Nothing here changes how the app is developed. The APK stays: it is how
this app reaches a phone today, it is how a build gets onto a handset
five minutes after a commit, and a store release cycle is far too slow to
debug a speaker with.

## Status

Not started. This file is the plan and the decision record; it is not a
description of something that exists.

| | |
| --- | --- |
| applicationId | `se.valfrid.openaudiolink` — **settled**, see below |
| Play Console account | not created |
| Signing key | not created |
| AAB build | not written |
| Listing, privacy policy, data safety | not written |

## The identity, and why it was settled first

**`se.valfrid.openaudiolink`.** It was `org.openaudiolink.phone` until
the day this file was written.

This is the one decision that cannot be revisited. Play keys the listing,
the reviews, the install base and the update path on the applicationId; a
different one is a different app that no existing installation upgrades
to. Changing it costs a reinstall today and is impossible after the first
upload, so it was changed today.

`namespace` in `app/build.gradle.kts` stays `org.openaudiolink.phone`.
It is where the generated `R` and `BuildConfig` classes live and it
follows the Kotlin source tree; it is not the store identity and does not
have to match it. Keeping them separate is what made the change a
one-line edit rather than a move of every file.

## What has to be fixed before the first upload

Five things, roughly in order of how badly each one bites. None of them
is in a generic Play checklist and all of them are specific to this app.

### 1. 16 KB page sizes — the one that will actually block the upload

Google Play requires apps with native code and `targetSdk` 35 or higher
to support 16 KB memory pages. This app ships `liblibrespot.so`, so it is
in scope.

`.github/workflows/librespot-android.yml` builds it with `cargo ndk -t
arm64-v8a --platform 26` and **does not pin an NDK version** — it uses
whatever `ANDROID_NDK_ROOT` the GitHub runner happens to export that
week. Whether the current binary is 16 KB aligned is therefore not a
question the repository can answer, which is its own problem: the answer
changes silently when GitHub updates its image.

Two things to do, and they are worth doing together:

- **Pin the NDK version** in that workflow, so the binary is reproducible
  and the alignment is a property of the repository rather than of the
  runner.
- **Link with `-Wl,-z,max-page-size=16384`** and verify the result rather
  than trusting the default. Recent NDKs align to 16 KB on their own;
  older ones do not, and "recent" is exactly what is not pinned here.

Verify with `llvm-readelf -l liblibrespot.so` and check the `LOAD`
segment alignment, in CI, as a build step that fails — the same
discipline `ci.yml` already applies to "is librespot actually in the
APK?", and for the same reason.

**This cannot be caught by testing.** The Galaxy A8 this app is developed
on uses 4 KB pages, so a misaligned binary works perfectly there and
fails on newer hardware — and Play rejects the bundle before any of that.

### 2. Spotify, via librespot — still open

**Decision deferred.** It needs making before the first upload and it is
the largest risk to the whole exercise, so it should not be made in a
hurry at the end.

The problem: Spotify Connect here is librespot, an unofficial client
built by reverse engineering. That is fine for something you build and
install yourself and it is a different question on a public store.

- Play policy covers apps that access a service in a way that breaches
  that service's terms. Spotify's terms do not permit unofficial clients.
- The listing cannot use Spotify's name or mark in a way that implies
  endorsement. The app already refuses to draw anything resembling their
  logo (`Glyphs.kt`); the store listing text needs the same care.
- Enforcement can arrive long after approval — a rights-holder complaint
  pulls a listing that reviewers passed, and by then the closed-testing
  period has been spent.

The options, unchanged from when the question was first asked:

1. **Ship it.** One build, everything works, accept that the listing may
   be rejected or later pulled.
2. **A Play build without librespot.** A Gradle product flavour: the AAB
   drops Spotify, the GitHub APK keeps it. The store version still does
   local files, internet radio, the test tone and vinyl-node control —
   which is a real product, just not the one this was built for.
3. **Ship it, with the flavour split already in place**, so removing
   librespot is a build-config change rather than a rewrite on a
   deadline.

Option 3 costs the least if the answer turns out to be no.

Worth being clear about one thing that is *not* a problem: librespot is
**bundled in the APK**, not downloaded at runtime. Play forbids an app
fetching and running executable code after installation; an executable
shipped inside the package and run from the native library directory is
not that. `useLegacyPackaging = true` exists precisely so there is a file
on disk to exec, and the reasoning is already written down in
`app/build.gradle.kts` — a reviewer asking about it should get that
answer.

### 3. Release signing is currently the debug key

`app/build.gradle.kts` gives the `release` build type
`signingConfig = signingConfigs.getByName("debug")`. That was right when
the only artefact was a debug APK and it is wrong the moment a bundle is
uploaded.

What it should become: a signing config driven from environment or
Gradle properties, which **fails the release build loudly** when they are
absent rather than quietly falling back to the debug key. A release
signed with the debug key that reaches Play is not recoverable.

The keystore never enters the repository. This is the same rule the
project already applies to the Wi-Fi credentials and the party PSK, for
the same reason and with more force: whoever holds the upload key can
publish an update that a phone installs over this one without asking.
Store it in GitHub Actions secrets and keep an offline copy — the secret
is not a backup.

Use **Play App Signing**, so the key held here is the upload key and
Google holds the app signing key. An upload key can be reset if it is
lost; an app signing key cannot.

### 4. The privacy policy needs a URL, not a file

Play requires a publicly reachable https address. `docs/PRIVACY.md` in
the repository is where it should be *written*, but a file in a git tree
is not a URL a reviewer can open. GitHub Pages off this repository is
enough and keeps git authoritative.

It has to be accurate, and this app has more to declare than its size
suggests:

- **Spotify credentials.** Signing in stores an OAuth token in the app's
  cache directory. It never leaves the device except to Spotify, and the
  *Forget account* button exists because the cast point belongs to
  whoever signed in — the screen already says so.
- **Local network access.** Multicast discovery, unicast control, and
  RTP audio to devices on the LAN.
- **Internet radio** fetches whatever URL the user enters.
- **Local audio files**, read and sent to speakers on the same network.
- **No analytics, no crash reporting, no telemetry, no server.** Worth
  stating plainly, because most privacy policies cannot.

The Data Safety form in Console must agree with this file word for word.

### 5. Two claims in the repository become false

`ci.yml` and `docs/PHONE-APP.md` both state that there is no store
listing and there will not be one. If this goes ahead, both need
updating — a comment that contradicts the build it documents is worse
than no comment.

## What is already right

Not everything needs work. Worth knowing so it does not get "fixed":

- **Foreground service.** `AndroidManifest.xml` already declares
  `android:foregroundServiceType="mediaPlayback"` with the matching
  `FOREGROUND_SERVICE_MEDIA_PLAYBACK` permission, which is what Android
  14+ requires. Console will still want a declaration justifying the
  type; the justification is true and easy — the app streams audio and
  must keep sending with the screen off.
- **Permissions are already minimal** and each one has its reason written
  beside it in the manifest. That text is most of the answer to Console's
  permission questions.
- **`allowBackup="false"`**, so a cached Spotify token is not swept into
  a cloud backup.
- **The version is now one number.** `versionName` in the build file,
  read by the app through `BuildConfig.VERSION_NAME`. It used to be
  carried a second time as a Kotlin constant and the two drifted.

## Things to expect to be asked

- **Cleartext HTTP.** `usesCleartextTraffic="true"` is app-wide because
  every node endpoint is plain HTTP on port 41001 and the addresses are
  whatever DHCP gave them, so there is no domain to scope a network
  security config to. The reason is in the manifest already. It is not a
  policy violation; it is a question.
- **arm64 only.** The app ships one ABI. With an App Bundle that is
  handled cleanly — Play serves it to arm64 devices and does not offer it
  elsewhere — but it does mean 32-bit-only and x86 devices will not see
  the listing at all.
- **Closed testing.** A personal developer account needs a closed test
  with a number of testers, sustained for a number of days, before
  production access. The figures change; read them in Console rather than
  trusting any number written here or anywhere else.

## The build, once the above is settled

Keep the current jobs and add to them. Every commit, as now:

    compile → :core:test → lint → :app:assembleDebug → APK artifact

On a tag:

    tag v1.0.0 → tests → lint → APK → bundleRelease → signed AAB
               → GitHub Release → (manually) Play

**`versionCode` from the tag.** Play refuses a bundle whose code is not
higher than the last, and a hand-maintained integer is a thing to forget
at the worst moment. Deriving both `versionName` and `versionCode` from
the tag makes the git history the source of truth, which is the stated
goal of the whole exercise.

**The Play upload stays manual until it has been done by hand at least
once.** Automating a step nobody has performed hides its failure modes,
and the first upload is the one that establishes App Signing. Once the
Play Developer API is wired up, production should still be an explicit
action rather than something a push can cause.

## Order of work

Nothing here needs a Play account, and none of it is wasted if the
Spotify decision goes the other way:

1. ~~Settle the applicationId.~~ Done: `se.valfrid.openaudiolink`.
2. ~~One source for the version.~~ Done: `BuildConfig.VERSION_NAME`.
3. Pin the NDK and prove 16 KB alignment in CI.
4. Add lint to the build.
5. Real release signing, failing loudly without credentials.
6. `versionName` and `versionCode` derived from the tag.
7. `PRIVACY.md`, `TESTING.md`, `CHANGELOG.md`, and the listing text.
8. Decide the Spotify question.
9. Produce and install a signed AAB before any account exists —
   `bundletool build-apks --local-testing` puts one on a real phone.

Only then create the Play Console app, and only with the final
applicationId. A package name used for an experiment is burnt.
