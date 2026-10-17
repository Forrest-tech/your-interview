// 在已登录的 your-interview 页面控制台里运行
// 把 AI Practice "Tech Stack" 分类的材料迁到 Tech Stack (Knowledge)
(async () => {
  const api = async (method, path, body) => {
    const r = await fetch(path, {
      method,
      headers: { 'Content-Type': 'application/json' },
      body: body ? JSON.stringify(body) : undefined,
      credentials: 'include'
    });
    if (!r.ok) throw new Error(`${method} ${path} -> ${r.status}`);
    const t = await r.text();
    return t ? JSON.parse(t) : null;
  };

  // 1. 找 Tech Stack 分类
  const cats = await api('GET', '/api/practice/categories');
  const list = Array.isArray(cats) ? cats : (cats.items || cats.data || []);
  const tsCat = list.find(c => (c.name || '').toLowerCase().includes('tech stack'));
  if (!tsCat) throw new Error('找不到 Tech Stack 分类');
  console.log('分类:', tsCat.name, tsCat.id);

  // 2. 拿材料树,按分类过滤
  const tree = await api('GET', '/api/practice/materials');
  const nodes = Array.isArray(tree) ? tree : (tree.items || tree.nodes || tree.data || []);
  const inCat = nodes.filter(n => n.categoryId === tsCat.id || n.categoryID === tsCat.id);
  console.log('该分类节点数:', inCat.length);

  // 3. 建 id->节点映射,找文件夹
  const byId = new Map(inCat.map(n => [n.id, n]));
  const folders = new Map(); // folderId -> folderName
  inCat.forEach(n => {
    const kind = (n.kind || '').toLowerCase();
    if (kind.includes('folder')) folders.set(n.id, n.name);
  });

  // 4. 文件 -> Knowledge 条目
  let created = 0, skipped = 0;
  for (const n of inCat) {
    const kind = (n.kind || '').toLowerCase();
    if (kind.includes('folder')) continue;
    const parent = n.parentId ? byId.get(n.parentId) : null;
    const topic = parent ? parent.name : (n.topic || 'Uncategorized');
    const title = n.name || 'Untitled';
    const content = n.content || '';

    // 去重:同 Topic+Title 已存在就跳过
    const existing = await api('GET', `/api/knowledge?search=${encodeURIComponent(title)}&pageSize=5`);
    const items = existing.items || existing.data || [];
    if (items.some(i => i.title === title && i.topic === topic)) {
      console.log('跳过(已存在):', topic, '-', title);
      skipped++;
      continue;
    }

    await api('POST', '/api/knowledge', {
      title,
      topic,
      question: title,
      conceptExplanation: content || null,
      source: 'AI Practice',
      difficulty: 3,
      importance: 3
    });
    console.log('已迁移:', topic, '-', title);
    created++;
  }
  console.log(`完成: 新建 ${created}, 跳过 ${skipped}`);
  return { created, skipped };
})();
