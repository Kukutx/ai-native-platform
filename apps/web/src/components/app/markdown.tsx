import Link from 'next/link';
import ReactMarkdown, { type Components } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { cn } from '@/lib/utils';

/** Markdown 语法树里用到的部分。 */
interface MarkdownNode {
  type: string;
  value?: string;
  children?: MarkdownNode[];
}

/**
 * 段落里的单个换行也显示为换行（与 GitHub 评论一致）：评论、上传文档里分行写的内容不会挤成一段。
 * 只处理普通文字，代码块和行内代码不受影响。
 */
function remarkSoftBreaks() {
  const visit = (node: MarkdownNode) => {
    if (!node.children) {
      return;
    }

    node.children = node.children.flatMap((child): MarkdownNode[] => {
      if (child.type === 'text' && child.value?.includes('\n')) {
        return child.value.split('\n').flatMap((line, i) => (i === 0 ? [{ type: 'text', value: line }] : [{ type: 'break' }, { type: 'text', value: line }]));
      }

      visit(child);
      return [child];
    });
  };

  return (tree: MarkdownNode) => visit(tree);
}

const remarkPlugins = [remarkGfm, remarkSoftBreaks];

/** 应用内的路径（例如智能体回复里的 /tasks/view?id=…）在当前窗口打开，其余链接在新窗口打开。 */
function isInternal(href: string) {
  return href.startsWith('/') && !href.startsWith('//');
}

/**
 * 各元素的样式与主题一致（颜色都取主题变量，深色模式自动适配）。
 * react-markdown 会给组件传 node（语法树节点），不能透传到 DOM 上，所以这里逐个取需要的属性。
 */
const components: Components = {
  h1: ({ children }) => <h3 className='mt-5 mb-2 text-lg font-semibold first:mt-0'>{children}</h3>,
  h2: ({ children }) => <h4 className='mt-5 mb-2 text-base font-semibold first:mt-0'>{children}</h4>,
  h3: ({ children }) => <h5 className='mt-4 mb-1.5 font-semibold first:mt-0'>{children}</h5>,
  h4: ({ children }) => <h6 className='mt-4 mb-1.5 font-medium first:mt-0'>{children}</h6>,
  h5: ({ children }) => <p className='mt-3 mb-1 font-medium first:mt-0'>{children}</p>,
  h6: ({ children }) => <p className='text-muted-foreground mt-3 mb-1 font-medium first:mt-0'>{children}</p>,
  p: ({ children }) => <p className='my-2 first:mt-0 last:mb-0'>{children}</p>,
  a: ({ href, children }) => {
    if (!href) {
      return <span>{children}</span>;
    }

    const className = 'text-primary font-medium underline underline-offset-4 [overflow-wrap:anywhere]';
    return isInternal(href) ? (
      <Link href={href} className={className}>
        {children}
      </Link>
    ) : (
      <a href={href} target='_blank' rel='noreferrer' className={className}>
        {children}
      </a>
    );
  },
  ul: ({ children, className }) => (
    <ul className={cn('my-2 list-disc space-y-1 pl-5 first:mt-0 last:mb-0', className?.includes('contains-task-list') && 'list-none pl-1')}>{children}</ul>
  ),
  ol: ({ children, start }) => (
    <ol start={start} className='my-2 list-decimal space-y-1 pl-5 first:mt-0 last:mb-0'>
      {children}
    </ol>
  ),
  li: ({ children, className }) => <li className={cn('pl-0.5', className?.includes('task-list-item') && '[&>input]:mr-1.5 [&>input]:align-middle')}>{children}</li>,
  input: ({ type, checked }) => (type === 'checkbox' ? <input type='checkbox' checked={!!checked} disabled readOnly className='accent-primary size-3.5' /> : null),
  blockquote: ({ children }) => <blockquote className='text-muted-foreground my-2 border-l-2 pl-3 first:mt-0 last:mb-0'>{children}</blockquote>,
  hr: () => <hr className='my-4' />,
  pre: ({ children }) => (
    <pre className='bg-muted/60 my-2 overflow-x-auto rounded-md border p-3 font-mono text-xs leading-relaxed first:mt-0 last:mb-0 [&>code]:bg-transparent [&>code]:p-0'>
      {children}
    </pre>
  ),
  code: ({ children, className }) => <code className={cn('bg-muted rounded px-1 py-0.5 font-mono text-[0.85em]', className)}>{children}</code>,
  table: ({ children }) => (
    <div className='my-2 max-w-full overflow-x-auto first:mt-0 last:mb-0'>
      <table className='w-full border-collapse text-left text-[0.95em]'>{children}</table>
    </div>
  ),
  thead: ({ children }) => <thead className='bg-muted/60'>{children}</thead>,
  th: ({ children, style }) => <th style={style} className='border px-2 py-1 font-medium'>{children}</th>,
  td: ({ children, style }) => <td style={style} className='border px-2 py-1 align-top'>{children}</td>,
  // 不加载外部图片（避免泄露访问信息、撑乱版面），改成指向图片的链接。
  img: ({ src, alt }) =>
    typeof src === 'string' && src ? (
      <a href={src} target='_blank' rel='noreferrer' className='text-primary underline underline-offset-4'>
        图片：{alt || src}
      </a>
    ) : null,
};

/**
 * 按 Markdown（含 GFM 表格、任务列表、删除线、自动链接）渲染文本：对话回复、任务描述与评论、知识库正文共用。
 * 段落里的单个换行保留为换行。
 * 不渲染原始 HTML（照原样显示为文字），链接地址经过默认的安全过滤（去掉 javascript: 等）。
 */
export function Markdown({ children, className }: { children: string; className?: string }) {
  return (
    <div className={cn('min-w-0 text-sm leading-relaxed break-words', className)}>
      <ReactMarkdown remarkPlugins={remarkPlugins} components={components}>
        {children}
      </ReactMarkdown>
    </div>
  );
}
