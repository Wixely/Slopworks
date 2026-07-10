# Slopworks

A desktop tool that sets up everything needed to run [vLLM](https://github.com/vllm-project/vllm)
on a Windows machine — and then manages the running server.

vLLM has no native Windows support. Slopworks converges your machine to the one path that
works well: WSL2 → a dedicated, self-contained Linux distro → Podman → the official
`vllm/vllm-openai` container image, serving an OpenAI-compatible API on `localhost`.

## Principles

- **Convergent, not scripted.** Every setup step detects its current state
  (Missing / Partial / Broken / Ok) and plans only the actions needed to reach Ok.
  Repair is the same operation as install. Partially broken setups are fixed, not
  reinstalled from scratch.
- **One directory.** Config, state, downloads, logs, and the entire Linux distro
  (a single `ext4.vhdx`) live under one root folder. Uninstall removes everything —
  optionally including WSL itself, with warnings when it is in use by other systems.
- **Safe by default.** Every external effect (command, download, system change) is shown
  verbatim for approval before it runs. Auto mode — one toggle — approves everything for
  unattended setup.
- **No host Python or Node.js.** Everything language-runtime-shaped stays inside the container.
- **Every endpoint overridable.** Download URLs come from config; GitHub-hosted artifacts
  can be auto-resolved to the latest release.

## License

MIT — see [LICENSE](LICENSE). Third-party software that Slopworks downloads or invokes at
setup time (Ubuntu, Podman, NVIDIA toolkit, vLLM, models) is licensed by its respective
owners and is never bundled with Slopworks.
