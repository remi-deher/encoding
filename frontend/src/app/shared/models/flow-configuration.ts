import type {
  FlowComponentConfiguration,
  FlowComponentConfigurationField,
  FlowComponentConfigurationValue,
} from './flow-component';
export function flowConfigurationValue(
  field: FlowComponentConfigurationField,
  values: FlowComponentConfiguration,
): FlowComponentConfigurationValue {
  if (Object.prototype.hasOwnProperty.call(values, field.id)) return values[field.id];
  return (
    field.defaultValue ?? (field.type === 'boolean' ? false : field.type === 'number' ? null : '')
  );
}
