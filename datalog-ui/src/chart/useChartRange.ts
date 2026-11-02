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

import { onScopeDispose, reactive } from "vue";
import { TIME_SPANS } from "./bounds";
import { createRangeController, type RangeControllerOptions, type RangeHeader } from "./rangeController";

/** A range controller whose toolbar fields are reactive; it stops with the component. */
export function useChartRange(options: Omit<RangeControllerOptions, "onHeader">) {
  const header = reactive<RangeHeader>({
    startDate: "",
    minDate: "",
    timespanExact: true,
    timespanSeconds: TIME_SPANS[2].seconds,
    timespanLabel: ""
  });

  const range = createRangeController({
    ...options,
    onHeader: (next) => Object.assign(header, next)
  });

  onScopeDispose(() => range.dispose());
  return { range, header };
}
