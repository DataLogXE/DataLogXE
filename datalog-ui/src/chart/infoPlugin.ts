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

import type uPlot from "uplot";

/**
 * The name and the badge of a plot lie in its .u-over. The controller builds the plot again when the kind of tag
 * changes, so the element moves into every new .u-over and back to its host before uPlot removes it.
 */
export function infoPlugin(getInfo: () => HTMLElement | null, getHost: () => HTMLElement | null): uPlot.Plugin {
  return {
    hooks: {
      ready: (plot) => {
        const info = getInfo();
        if (info) {
          plot.over.appendChild(info);
        }
      },
      destroy: () => {
        const info = getInfo();
        const host = getHost();
        if (info && host) {
          host.insertBefore(info, host.firstChild);
        }
      }
    }
  };
}
