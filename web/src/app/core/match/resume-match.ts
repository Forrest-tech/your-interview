/**
 * 简历 × JD 关键词匹配 —— Tracker 与 Playbook 共用同一份实现(同源同值)。
 *
 * 原先这段算法只活在 Tracker 组件里,Playbook 条目头读的是另一个落库字段
 * resumeScore,两边口径不一致导致 Playbook 永远显示 “—”。
 * 抽到 core 后:两处都用「有效简历文本(投递版优先)+ JD(全文优先、回落摘要)」
 * 喂同一函数,分数必然一致;谁也不许再私自另算一套。
 */

// ============================================================================
//  关键词匹配分析(纯算法,不调 AI)
//  Forrest 2026-09-18:只算技术词;软素质词单列不计分。
// ============================================================================

/** 匹配分析结果。 */
export interface MatchResult {
  /** 0-100 的技术词命中率 —— 这是主分数。 */
  score: number;
  /** JD 里出现且简历也有的技术词。 */
  hit: string[];
  /** JD 里出现但简历没有的技术词 —— 即 Simplify 的 "Missing Keywords"。 */
  missing: string[];
  /** JD 里识别到的技术词总数(分数的分母)。 */
  total: number;
  /** 顺带识别出的软素质词 —— 单列展示,不进分数。 */
  softSkills: string[];
}

/**
 * 技术关键词词典。
 * ⚠️ 只收"能在简历里当技能写"的词 —— 判断标准:
 *    招聘方能拿它做筛选条件,候选人能拿它做技能声明。
 *    收词原则:宁可少收,不可乱收(乱收会让分母虚高、分数虚低)。
 */
const TECH_TERMS: readonly string[] = [
  // 语言
  'C#', 'C\+\+', 'Java', 'Python', 'JavaScript', 'TypeScript', 'SQL', 'T-SQL', 'TSQL',
  'Go', 'Rust', 'Ruby', 'PHP', 'Kotlin', 'Swift', 'Scala', 'Perl', 'Bash', 'PowerShell',
  'HTML', 'CSS', 'SCSS', 'SASS', 'XAML', 'JSON', 'XML', 'YAML',
  // 后端 / 框架
  '.NET', '.NET Core', 'ASP.NET', 'Web API', 'EF Core', 'Entity Framework', 'WCF', 'WPF',
  'Node.js', 'Express', 'Spring', 'Django', 'Flask', 'FastAPI', 'gRPC', 'REST', 'RESTful',
  'GraphQL', 'WebSockets', 'SignalR', 'Prism', 'MVVM', 'MVC',
  // 前端
  'Angular', 'React', 'Vue', 'Redux', 'RxJS', 'NgRx', 'jQuery', 'Tailwind', 'Bootstrap',
  'Material', 'Webpack', 'Vite', 'ES6', 'SASS',
  // 数据库 / 缓存
  'SQL Server', 'PostgreSQL', 'MySQL', 'Oracle', 'MongoDB', 'DynamoDB', 'Redis', 'Memcached',
  'Elasticsearch', 'Cassandra', 'SQLite', 'Cosmos DB', 'BigQuery', 'Snowflake', 'Redshift',
  // 云 / 基础设施
  'Azure', 'AWS', 'GCP', 'Google Cloud', 'Compute Engine', 'App Services', 'Service Bus',
  'S3', 'EC2', 'ECS', 'Fargate', 'Lambda', 'Kubernetes', 'Docker', 'Terraform', 'Helm',
  'PaaS', 'IaaS', 'SaaS', 'Serverless',
  // 架构 / 方法
  'Microservices', 'Clean Architecture', 'DDD', 'Domain-Driven Design', 'SOLID', 'CQRS',
  'Event Sourcing', 'Strangler', 'Design Patterns', 'Distributed', 'High Availability',
  'Scalability', 'Load Balancing', 'Caching', 'Message Queue', 'Kafka', 'RabbitMQ',
  // 测试 / 质量
  'Unit Testing', 'TDD', 'xUnit', 'NUnit', 'MSTest', 'Jest', 'Jasmine', 'Karma', 'Cypress',
  'Playwright', 'Selenium', 'SonarQube', 'Code Review', 'Integration Testing',
  // DevOps
  'CI/CD', 'GitHub Actions', 'Jenkins', 'TeamCity', 'Azure DevOps', 'GitLab', 'Git',
  'Observability', 'Prometheus', 'Grafana', 'Datadog', 'New Relic', 'Splunk',
  // 安全 / 合规
  'OWASP', 'Security', 'OAuth', 'JWT', 'SSO', 'SAML', 'FedRAMP', 'FIPS',
  // 其他
  'Linux', 'Unix', 'Multithreading', 'Asynchronous', 'Object-Oriented', 'OOP',
  'Agile', 'Scrum', 'Kanban', 'Jira', 'Confluence', 'AI', 'Machine Learning', 'LLM',
  'Web Application', 'Web Services', 'Full-Stack', 'Full Stack', 'Front-End', 'Back-End'
];

/**
 * 软素质词 —— 单独识别、单独展示,但**不进分数**。
 * 理由:简历里不会把"团队合作"当技能写,算进分母会让所有人得分虚低。
 */
const SOFT_TERMS: readonly string[] = [
  'team player', 'employee engagement', 'communication skills', 'self-starter',
  'problem-solving', 'analytical', 'attention to detail', 'collaboration',
  'fast-paced', 'entrepreneurial', 'work well under pressure', 'written and verbal',
  'interpersonal', 'time management', 'adaptable', 'proactive', 'mentoring',
  'stakeholder', 'leadership',
  '团队合作', '沟通能力', '抗压', '责任心', '学习能力'
];

/** 归一化:小写 + 折叠空白,用于跨大小写、跨连字符比对。 */
function norm(t: string): string {
  return t.toLowerCase().replace(/\s+/g, ' ').trim();
}

/**
 * 常见别名归一 —— 让 "ASP.NET" 和 "ASP .NET"、"C Sharp" 与 "C#"
 * 这类写法差异不会把命中判成未命中。
 */
function aliasesOf(term: string): string[] {
  const n = norm(term);
  const out = [n];
  if (n === 'c#') out.push('c sharp', 'csharp');
  if (n === '.net' || n === '.net core') out.push('.net', '.net core', 'dotnet', 'asp.net');
  if (n === 'asp.net') out.push('asp.net', 'aspnet', 'asp .net');
  if (n === 'rest' || n === 'restful') out.push('rest', 'restful');
  if (n === 'ci/cd') out.push('ci/cd', 'ci cd', 'continuous integration');
  if (n === 'full-stack' || n === 'full stack') out.push('full-stack', 'full stack', 'fullstack');
  if (n === 'gcp' || n === 'google cloud') out.push('gcp', 'google cloud');
  if (n === 'dd' + 'd') out.push('ddd', 'domain-driven design', 'domain driven design');
  if (n === 'unit testing') out.push('unit test', 'unit testing', 'unit tests');
  if (n === 't-sql' || n === 'tsql') out.push('t-sql', 'tsql', 't sql');
  return Array.from(new Set(out));
}

/** 该词是否在文本里出现(大小写不敏感 + 别名)。 */
function occursIn(text: string, term: string): boolean {
  const hay = norm(text);
  return aliasesOf(term).some((a) => hay.includes(a));
}

/**
 * 计算简历与 JD 的匹配度。
 *
 * @param resume 简历全文
 * @param jd     JD 全文
 */
export function computeMatch(resume: string, jd: string): MatchResult {
  // 只把"JD 里真的出现过"的技术词算进分母 ——
  // 词典有几百个词,全算分母会变成"词典命中率",毫无意义。
  const jdTech = TECH_TERMS.filter((t) => occursIn(jd, t));

  const hit: string[] = [];
  const missing: string[] = [];
  for (const t of jdTech) {
    if (occursIn(resume, t)) hit.push(t);
    else missing.push(t);
  }

  const total = jdTech.length;
  const score = total === 0 ? 0 : Math.round((hit.length / total) * 100);

  const softSkills = SOFT_TERMS.filter((t) => occursIn(jd, t));

  return { score, hit, missing, total, softSkills };
}

