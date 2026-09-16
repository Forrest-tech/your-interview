import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';

/** 页脚三个链接共用一个组件,靠 URL 区分内容。 */
type LegalKind = 'terms' | 'privacy' | 'copyright';

interface LegalDoc {
  title: string;
  updated: string;
  sections: { h: string; ps: string[] }[];
}

/**
 * 法律条款页(条款 / 隐私 / 版权)。
 *
 * 2026-09-15 第七轮新增 —— 页脚放上了「条款 | 隐私 | 版权」三个链接,
 * 但路由不存在,点过去会走 `**` 重定向回 dashboard(等价于点不动)。
 * 故补建一个按 URL 分支的通用页面,三个路径共用。
 *
 * ⚠️ 内容边界:以下文本为通用模板描述本站实际做法(不联网、凭据服务端保管、
 *    录音本地存储),不构成法律意见。上线前建议由安省持牌律师审阅。
 */
@Component({
  selector: 'app-legal',
  standalone: true,
  imports: [CommonModule, RouterLink, MatIconModule],
  templateUrl: './legal.component.html',
  styleUrl: './legal.component.scss'
})
export class LegalComponent {
  private readonly route = inject(ActivatedRoute);

  /** 当前文档类型,由 URL 决定(terms / privacy / copyright)。 */
  readonly kind = toSignal(
    this.route.url.pipe(
      map((segs) => (segs[0]?.path ?? 'terms') as LegalKind)
    ),
    { initialValue: 'terms' as LegalKind }
  );

  readonly docs: Record<LegalKind, LegalDoc> = {
    terms: {
      title: '服务条款',
      updated: '最后更新：2026 年 9 月',
      sections: [
        {
          h: '1. 服务说明',
          ps: [
            'Your Interview 是一个面向个人求职者的面试练习与资料整理工具,提供口述素材管理、朗读示范与发音评测辅助。',
            '本服务不提供任何形式的求职结果担保。练习评分仅用于辅助自我改进,不代表用人单位的真实评价标准。'
          ]
        },
        {
          h: '2. 账户与使用',
          ps: [
            '您需对账户下发生的全部活动负责,并妥善保管登录凭据。',
            '禁止将本服务用于任何违法用途,或以自动化方式批量抓取、干扰服务正常运行。'
          ]
        },
        {
          h: '3. 付费与退款',
          ps: [
            '付费功能的计费周期、价格以购买时的页面展示为准。',
            '您的购买受加拿大适用的消费者保护法律保护；您的订单将根据安大略省的法律进行处理。'
          ]
        },
        {
          h: '4. 免责声明',
          ps: [
            '发音评测结果依赖第三方语音服务(Azure Speech)返回的数据,我们不对其准确性作出承诺。',
            '在法律允许的最大范围内,本服务按"现状"提供,不作任何明示或默示的保证。'
          ]
        }
      ]
    },
    privacy: {
      title: '隐私政策',
      updated: '最后更新：2026 年 9 月',
      sections: [
        {
          h: '1. 我们收集什么',
          ps: [
            '账户信息：用户名、显示名称与角色权限。',
            '您主动录入的求职素材文本(自我介绍、项目经历等)。',
            '练习录音与评测结果。'
          ]
        },
        {
          h: '2. 录音与评测数据',
          ps: [
            '练习录音用于生成发音评测结果,评测请求由服务端转发至 Azure Speech 处理。',
            'Azure Speech 密钥仅保存在服务端,不会下发到浏览器,也不会随请求暴露给您的前端。'
          ]
        },
        {
          h: '3. 我们不做什么',
          ps: [
            '我们不会将您的素材文本或录音出售给第三方。',
            '我们不会将您的测评结果用于除改进本服务之外的目的。'
          ]
        },
        {
          h: '4. 您的权利',
          ps: [
            '您可以随时查看、修改或删除自己录入的素材与录音。',
            '根据加拿大《个人信息保护与电子文件法》(PIPEDA),您有权请求查阅我们所持有的您的个人信息。',
            '如需行使上述权利,请联系 forrestlin2024@gmail.com。'
          ]
        },
        {
          h: '5. 数据保留地',
          ps: [
            '服务部署于加拿大区域。语音评测所调用的 Azure 资源同样配置在加拿大数据中心区域内。'
          ]
        }
      ]
    },
    copyright: {
      title: '版权声明',
      updated: '最后更新：2026 年 9 月',
      sections: [
        {
          h: '1. 权利归属',
          ps: [
            '© 2026 Your Interview · All Rights Reserved',
            '本站的界面设计、源代码、文案与图形标识的著作权归本服务运营方所有。'
          ]
        },
        {
          h: '2. 您的内容归您',
          ps: [
            '您录入的求职素材、简历内容与练习录音始终归您所有。',
            '我们仅在为您提供服务的必要范围内存储与处理这些内容,不主张任何额外权利。'
          ]
        },
        {
          h: '3. 第三方组件',
          ps: [
            '本站使用 Angular、.NET、PostgreSQL 等开源软件,相关著作权归各自权利人所有。',
            '发音评测能力由 Microsoft Azure Speech 提供,相关商标与服务条款归 Microsoft 所有。'
          ]
        },
        {
          h: '4. 侵权处理',
          ps: [
            '如您认为本站内容侵犯了您的合法权益,请通过 forrestlin2024@gmail.com 与我们联系,我们将及时处理。'
          ]
        }
      ]
    }
  };
}
