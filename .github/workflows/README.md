# CI/CD pipeline — ChatVerse backend

## Flow

```
You push to `dev` (from VS Code / Visual Studio)
   ↓
ci.yml runs   — restores NuGet, builds Release. ~2-3 min.
   ↓
On green ✅, auto-merge.yml fires
   ↓
master gets fast-forwarded to dev, push triggers
   ↓
Render redeploys backend (~5-7 min cold-build)
```

If CI **fails**, master stays untouched. Fix on `dev`, re-push.

## What runs

| File             | Trigger              | What it does                          |
| ---------------- | -------------------- | ------------------------------------- |
| `ci.yml`         | push to `dev`, any PR| `dotnet restore` + `dotnet build -c Release` |
| `auto-merge.yml` | `ci.yml` green on `dev` push | `git merge --ff-only dev` → push master |

## Required GitHub repo settings

1. **Settings → Actions → General** → "Workflow permissions" →
   - select **"Read and write permissions"**.
   - Tick **"Allow GitHub Actions to create and approve pull requests"**.
2. **Settings → Branches** → leave `master` UNPROTECTED for now
   (default GITHUB_TOKEN can push). If you later add branch
   protection, swap `${{ secrets.GITHUB_TOKEN }}` in `auto-merge.yml`
   for a fine-grained PAT stored as `MERGE_PAT`.

## How to make a change

```bash
git checkout dev
# … edit code …
git add . && git commit -m "feat: …"
git push origin dev
```

That's it. Watch the Actions tab — CI runs → auto-merge runs → Render redeploys.

## When auto-merge refuses

If you see "main and dev have diverged" the workflow exited
with an error code on purpose. That means someone (probably you)
pushed directly to `master`. Fix:

```bash
git checkout dev
git pull origin master --rebase   # bring master commits onto dev
git push origin dev                # re-trigger CI, auto-merge will succeed
```
