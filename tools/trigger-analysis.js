#!/usr/bin/env node
/**
 * 触发一次真实的面试录音分析(往 RabbitMQ 发 InterviewAnalysisRequested 事件)。
 *
 *   node tools/trigger-analysis.js <interviewEntryId> [audioPath]
 *
 * 为什么要这个脚本:Worker 的输入是消息,手工测一次就得往队列里丢一条。
 * 有这个脚本,排障时不必启动完整 UI 就能验证整条管线。
 */
const amqp = require('amqplib');

(async () => {
  const entryId = process.argv[2];
  const audioPath = process.argv[3] || null;

  if (!entryId) {
    console.error('用法: node trigger-analysis.js <interviewEntryId> [audioPath]');
    process.exit(1);
  }

  const conn = await amqp.connect('amqp://guest:guest@127.0.0.1:5672');
  const ch = await conn.createChannel();

  // MassTransit 用 exchange 扇出;Worker 的消费者队列绑定在它上面
  // MassTransit 的 exchange 名是完整的类型 URN,不是简单类名
  const exchange = 'YourInterview.SharedContracts.Events:InterviewAnalysisRequested';

  const message = {
    messageId: require('crypto').randomUUID(),
    correlationId: entryId,
    conversationId: entryId,
    sourceAddress: 'rabbitmq://localhost/trigger',
    destinationAddress: `rabbitmq://localhost/${exchange}`,
    messageType: ['urn:message:YourInterview.SharedContracts.Events:InterviewAnalysisRequested'],
    sentTime: new Date().toISOString(),
    message: {
      interviewEntryId: entryId,
      assetId: '00000000-0000-0000-0000-000000000001',
      userId: '00000000-0000-0000-0000-000000000000',
      storagePath: audioPath,
      transcriptText: null,
    },
  };

  await ch.assertExchange(exchange, 'fanout', { durable: true });
  ch.publish(exchange, '', Buffer.from(JSON.stringify(message)), {
    contentType: 'application/vnd.masstransit+json',
    persistent: true,
    messageId: message.messageId,
  });

  console.log('已投递分析请求 entryId=' + entryId);
  await ch.close();
  await conn.close();
})().catch((e) => { console.error('ERR', e.message); process.exit(1); });
