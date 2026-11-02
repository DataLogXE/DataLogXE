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

/** Runs at most `limit` tasks at once; the others wait in order. */
export interface FetchQueue {
  run<T>(task: () => Promise<T>, signal: AbortSignal, onStart?: () => void): Promise<T>;
  /** Rejects every waiting task as aborted. Running tasks end through their own signal. */
  clear(): void;
}

interface Waiting {
  start: () => void;
  cancel: () => void;
}

function abortError(): DOMException {
  return new DOMException("The request was cancelled.", "AbortError");
}

export function createFetchQueue(limit: number): FetchQueue {
  let active = 0;
  const waiting: Waiting[] = [];

  function next() {
    while (active < limit && waiting.length > 0) {
      waiting.shift()!.start();
    }
  }

  return {
    run<T>(task: () => Promise<T>, signal: AbortSignal, onStart?: () => void): Promise<T> {
      return new Promise<T>((resolve, reject) => {
        if (signal.aborted) {
          reject(abortError());
          return;
        }

        const onAbort = () => {
          const index = waiting.indexOf(item);
          if (index >= 0) {
            waiting.splice(index, 1);
            reject(abortError());
          }
        };

        const item: Waiting = {
          start: () => {
            signal.removeEventListener("abort", onAbort);
            active += 1;
            onStart?.();
            task().then(resolve, reject).finally(() => {
              active -= 1;
              next();
            });
          },
          cancel: () => {
            signal.removeEventListener("abort", onAbort);
            reject(abortError());
          }
        };

        if (active < limit) {
          item.start();
          return;
        }
        signal.addEventListener("abort", onAbort, { once: true });
        waiting.push(item);
      });
    },
    clear() {
      const items = waiting.splice(0);
      for (const item of items) {
        item.cancel();
      }
    }
  };
}
