// Import only what the charts use, so the bundle does not carry all of ECharts.
import * as echarts from 'echarts/core';
import { LineChart } from 'echarts/charts';
import { GridComponent, TooltipComponent } from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';

echarts.use([LineChart, GridComponent, TooltipComponent, CanvasRenderer]);

export { echarts };
export type ChartOption = echarts.EChartsCoreOption;
