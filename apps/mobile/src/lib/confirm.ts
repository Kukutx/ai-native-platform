import { Alert, Platform } from 'react-native';

interface ConfirmOptions {
  title: string;
  message?: string;
  confirmText?: string;
  cancelText?: string;
  /** 删除、取消运行这类不可撤销的操作，确认按钮显示为警示样式（iOS 红色）。 */
  destructive?: boolean;
  onConfirm: () => void;
}

/** 二次确认。原生端用系统对话框；Web 预览里 Alert 不支持按钮，退回浏览器自带的 confirm。 */
export function confirm({ title, message, confirmText = '确定', cancelText = '取消', destructive, onConfirm }: ConfirmOptions) {
  if (Platform.OS === 'web') {
    if (globalThis.confirm?.(message ? `${title}\n\n${message}` : title)) {
      onConfirm();
    }

    return;
  }

  Alert.alert(title, message, [
    { text: cancelText, style: 'cancel' },
    { text: confirmText, style: destructive ? 'destructive' : 'default', onPress: onConfirm },
  ]);
}
