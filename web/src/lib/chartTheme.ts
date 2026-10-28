export const COLORS = {
  text: '#8b98a9',
  grid: '#243040',
  accent: '#4cc9f0',
  band: '#6ea8fe',
};

/** Shared axes and tooltip for every time-series chart. */
export function baseOption(unit: string | null) {
  return {
    animation: false,
    textStyle: { color: COLORS.text },
    grid: { left: 52, right: 16, top: 20, bottom: 28 },
    tooltip: {
      trigger: 'axis',
      backgroundColor: '#18212d',
      borderColor: COLORS.grid,
      textStyle: { color: '#e6edf3', fontSize: 12 },
      axisPointer: { type: 'line', lineStyle: { color: COLORS.text } },
    },
    xAxis: {
      type: 'time',
      axisLine: { lineStyle: { color: COLORS.grid } },
      axisLabel: { color: COLORS.text },
      splitLine: { show: false },
    },
    yAxis: {
      type: 'value',
      scale: true,
      name: unit ?? '',
      nameTextStyle: { color: COLORS.text, align: 'left' },
      axisLabel: { color: COLORS.text },
      splitLine: { lineStyle: { color: COLORS.grid } },
    },
  };
}
