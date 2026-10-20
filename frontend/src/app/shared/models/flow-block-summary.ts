import { FlowGraphNode, getFlowConfigurationSchema } from './flow-component';
import { MediaType } from './media-type';
import { flowConfigurationValue } from './flow-configuration';
/** Only schema-declared scalar settings are shown; scripts, secrets and nested objects stay in the inspector. */
export function flowBlockSummary(node: FlowGraphNode, mediaType: MediaType): string {
  const fields =
    getFlowConfigurationSchema(node.component.configurationSchema, mediaType)?.fields ?? [];
  return fields
    .filter((field) => field.inline && field.type !== 'secret')
    .flatMap((field) => {
      const value = flowConfigurationValue(field, node.configuration);
      if (
        value === null ||
        value === undefined ||
        value === '' ||
        typeof value === 'object' ||
        (field.type === 'text' && field.multiline)
      )
        return [];
      const label =
        field.type === 'select'
          ? (field.options.find((option) => option.value === value)?.label ?? String(value))
          : typeof value === 'boolean'
            ? value
              ? 'Oui'
              : 'Non'
            : String(value);
      return [field.label + ' : ' + label];
    })
    .slice(0, 3)
    .join(' · ');
}
