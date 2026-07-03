# Changelog

All notable changes to the JuddHome plugin are documented here.

## [1.0.1] — 2026-07-03

### Fixed
- JuddHome Settings modal (and any future `[hidden]`-toggled modal) would
  never actually close: `.jh-modal { display: flex; }` overrode the browser's
  default `[hidden] { display: none; }` rule regardless of specificity,
  because author-stylesheet rules always beat user-agent defaults. Cancel and
  Save appeared to do nothing and the full-screen modal blocked all other
  interaction. Added an explicit `.jh-modal[hidden] { display: none; }` rule.

## [1.0.0] — 2026-07-03

### Added
- Initial release targeting Jellyfin 10.11.11 (.NET 9.0).
- Netflix/Disney+ style home screen replacing the vanilla Jellyfin home screen.
- Ten modular sections: Hero Banner, Continue Watching, Next Up, Because You
  Watched, Recommended For You, Latest Movies, Latest TV Shows, Watch Again,
  Popular In Your Library, My List.
- Weighted recommendation engine (genre 40% / community rating 25% /
  recently added 20% / actor match 15%) with in-memory per-user preference
  profiles refreshed every 6 hours (configurable).
- Per-user section order and visibility, stored server-side, editable from the
  home screen or the hamburger menu; server-wide defaults for new users.
- Admin configuration page: section toggles and default order, items per row,
  hero banner toggle and speed, recommendation refresh interval, per-user
  override list with reset-to-defaults.
- New-user fallbacks: hidden empty rows, Popular In Your Library expansion,
  top-rated hero banner when there is no watch history.
- REST API: /JuddHome/Sections, /JuddHome/Section/{type}, /JuddHome/Hero,
  /JuddHome/Recommendations, /JuddHome/Config/User, /JuddHome/Config/Admin,
  /JuddHome/Health, /JuddHome/MyList/Create, /JuddHome/PlayTarget/{itemId}.
- Health endpoint with degraded-status reporting and per-section refresh
  timestamps; startup never blocks Jellyfin from starting.
