#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION_FILE="$SCRIPT_DIR/../VERSION"

if [[ ! -f "$VERSION_FILE" ]]; then
  echo "Error: $VERSION_FILE not found" >&2
  exit 1
fi

CURRENT_VERSION=$(cat "$VERSION_FILE")
BUMP_TYPE="${1:-patch}"
PREV_TAG=$(git describe --tags --abbrev=0 2>/dev/null || echo "")

# Parse and bump version
IFS='.' read -r MAJOR MINOR PATCH <<< "$CURRENT_VERSION"
case $BUMP_TYPE in
  major) MAJOR=$((MAJOR+1)); MINOR=0; PATCH=0 ;;
  minor) MINOR=$((MINOR+1)); PATCH=0 ;;
  patch) PATCH=$((PATCH+1)) ;;
  *)
    echo "Usage: $0 {major|minor|patch}" >&2
    exit 1
    ;;
esac

NEW_VERSION="$MAJOR.$MINOR.$PATCH"

# Generate changelog from commits since last tag
if [[ -n "$PREV_TAG" ]]; then
  CHANGELOG=$(git log "$PREV_TAG"..HEAD --pretty=format:"- %s" 2>/dev/null | grep -E "^- (feat|fix|refactor|perf|docs)" || echo "No conventional commits")
else
  CHANGELOG=$(git log --pretty=format:"- %s" 2>/dev/null | grep -E "^- (feat|fix|refactor|perf|docs)" || echo "Initial release")
fi

# Update version file
echo "$NEW_VERSION" > "$VERSION_FILE"
echo "Updated $VERSION_FILE: $CURRENT_VERSION → $NEW_VERSION"

# Commit and tag
git add VERSION
git commit -m "chore: release $NEW_VERSION"
git tag -a "v$NEW_VERSION" -m "Release v$NEW_VERSION

$CHANGELOG"

echo "✓ Tagged v$NEW_VERSION"
echo ""
echo "Next steps:"
echo "  git push origin main"
echo "  git push origin v$NEW_VERSION"
