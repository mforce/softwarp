# GitHub App for factory agents

Agents act under this app's identity, never the maintainer's token (DESIGN.md §9 `identity`, §12).

Parts A to C change nothing for running agents, so they can be done at any time. Part D removes the maintainer's token from this machine. **Do part D only when no agent or worker is in flight.**

## A. Create the app (browser)

1. Open <https://github.com/settings/apps/new>.
2. **GitHub App name:** a unique name, for example `mforce-factory`. The slug becomes the bot login `mforce-factory[bot]`.
3. **Homepage URL:** any URL, for example the factory repo.
4. **Webhook:** clear **Active**. The factory polls; it needs no webhook.
5. **Repository permissions:**

   | Permission | Access | Why |
   |---|---|---|
   | Contents | Read and write | push branches |
   | Pull requests | Read and write | open PRs, reply on threads |
   | Issues | Read and write | labels and comments. Issue *creation* stays blocked in the factory's tracker binding. |
   | Checks | Read-only | read CI results |
   | Commit statuses | Read-only | read CI results |
   | Actions | Read-only | read workflow runs and logs |
   | Metadata | Read-only | required by GitHub |
   | Workflows | **decide** | Read and write lets agents change `.github/workflows/*`. Without it, PRs that change CI go to the maintainer. |
   | Administration | **No access** | so the app cannot change rulesets or branch protection |

6. **Where can this GitHub App be installed?** Only on this account.
7. Click **Create GitHub App**.

## B. Key and IDs

1. On the app page, note the **App ID**.
2. Under **Private keys**, click **Generate a private key**. Move the downloaded file and lock it down:
   ```bash
   mkdir -p ~/.config/factory && mv ~/Downloads/<app-name>.*.private-key.pem ~/.config/factory/app.pem && chmod 600 ~/.config/factory/app.pem
   ```
   Never paste the key into a chat or commit it.

## C. Install the app

1. On the app page, click **Install App**, then choose your account.
2. Choose **Only select repositories**, then `mforce/cluckwork` (add the factory repo later).
3. Note the **installation ID**: the number at the end of the URL, `https://github.com/settings/installations/<id>`.
4. Check that the app is **not** a bypass actor on any ruleset (Settings → Rules → Rulesets). It is not one by default.

## D. Remove your token from this machine (only when nothing is in flight)

1. Check that no agent is running: no busy sessions in Paseo or Claude Code, and `pgrep -af 'codex|claude'` shows only idle sessions.
2. Log out:
   ```bash
   gh auth logout --hostname github.com --user mforce
   ```
3. Look for other copies of a GitHub token, and remove each one you find:
   ```bash
   ls ~/.git-credentials 2>/dev/null
   env | grep -E '^(GH|GITHUB)_TOKEN='
   grep -nE '(GH|GITHUB)_TOKEN' ~/.bashrc ~/.profile ~/.zshrc 2>/dev/null
   ```
   Also check the GitHub MCP server's settings in Claude Code. A personal access token in its Authorization header is your identity too.
4. Verify:
   ```bash
   gh auth status                       # expect: not logged in
   git -C ~/dev/cluckwork push --dry-run   # expect: an authentication error
   ```
5. From now on, do your own GitHub work in the browser. Agents run as your Unix user, so they can read any token file you keep on this machine, including a second `gh` config folder.

## E. Give the factory these values

Tell the factory session these values (not the key itself):

- App ID
- installation ID
- app slug
- key path (`~/.config/factory/app.pem`)
- your Workflows choice

The factory then does the rest (build step 1):
- `factory token` creates a 1-hour installation token from the key and gives it to `gh` (`GH_TOKEN`) and to git (a credential helper).
- Agent worktrees commit as `<slug>[bot] <<bot-user-id>+<slug>[bot]@users.noreply.github.com>`.

## F. First-PR checks

- The PR author shows as `<slug>[bot]`.
- CodeRabbit reviews it. Some reviewers skip bot authors by default; if it does, change its settings.
- Required checks run, and you can merge it from the browser.
