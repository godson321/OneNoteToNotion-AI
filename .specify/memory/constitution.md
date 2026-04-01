# Project Constitution

## Core Values

1. **User Outcomes First**: Synced Notion content should be usable, readable, and traceable.
2. **Predictable Mapping**: OneNote-to-Notion mapping rules must be stable, configurable, and backward-compatible.
3. **Minimal Surprise**: Defaults should preserve existing expectations; changes must be opt-in and explicit.

## Technical Principles

### Architecture
- Keep parsing and mapping layers separated; the semantic model should not leak target-platform specifics.
- Inject mapping strategies via configuration instead of hard-coded behavior.

### Code Quality
- Preserve compatibility and provide clear fallback behavior.
- Log important degradations for diagnosis.

### Performance
- Avoid increasing Notion API calls significantly.
- Defaults should not add extra network requests.

## Decision Framework

When choosing between approaches:
1. Does it preserve user outcomes and predictable mapping?
2. Is it backward-compatible or does it provide a clear migration path?
3. Does it minimize impact on sync performance and stability?
