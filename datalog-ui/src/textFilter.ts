/*
DataLogXE - A simple and high performance data logger.
Copyright (C) 2026 datalogxe@gmail.com

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

export type TextFilter =
  | { kind: "none" }
  | { kind: "contains"; needle: string }
  | { kind: "wildcard"; pattern: string };

export function parseTextFilter(query: string): TextFilter {
  const trimmed = query.trim().replaceAll("\"", "");
  if (trimmed.length === 0) {
    return { kind: "none" };
  }

  if (hasWildcard(trimmed)) {
    return { kind: "wildcard", pattern: trimmed };
  }

  return { kind: "contains", needle: trimmed };
}

export function anyTextMatches(filter: TextFilter, values: Array<string | null | undefined>): boolean {
  if (filter.kind === "none") {
    return true;
  }

  for (const value of values) {
    if (textMatches(filter, value)) {
      return true;
    }
  }

  return false;
}

function textMatches(filter: TextFilter, value: string | null | undefined): boolean {
  const text = value ?? "";
  if (filter.kind === "contains") {
    return text.toLowerCase().includes(filter.needle.toLowerCase());
  }

  if (filter.kind === "wildcard") {
    return wildcardIncludes(text, filter.pattern);
  }

  return true;
}

function hasWildcard(query: string): boolean {
  return query.includes("*") || query.includes("?");
}

function wildcardIncludes(text: string, pattern: string): boolean {
  return globMatch(text.toLowerCase(), `*${pattern.toLowerCase()}*`);
}

function globMatch(text: string, pattern: string): boolean {
  let textIndex = 0;
  let patternIndex = 0;
  let starPatternIndex = -1;
  let starTextIndex = -1;

  while (textIndex < text.length) {
    const patternChar = pattern[patternIndex];
    if (patternIndex < pattern.length && (patternChar === "?" || patternChar === text[textIndex])) {
      textIndex += 1;
      patternIndex += 1;
      continue;
    }

    if (patternIndex < pattern.length && patternChar === "*") {
      starPatternIndex = patternIndex;
      starTextIndex = textIndex;
      patternIndex += 1;
      continue;
    }

    if (starPatternIndex >= 0) {
      patternIndex = starPatternIndex + 1;
      starTextIndex += 1;
      textIndex = starTextIndex;
      continue;
    }

    return false;
  }

  while (patternIndex < pattern.length && pattern[patternIndex] === "*") {
    patternIndex += 1;
  }

  return patternIndex === pattern.length;
}
